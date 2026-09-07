using Microsoft.Data.Sqlite;
using MoneyCounter.Infrastructure.Operations;
using MoneyCounter.Infrastructure.Maintenance;

namespace MoneyCounter.Infrastructure.Storage;

public sealed class DbStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private Task? _disposeTask;

    public DbStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 5, Pooling = false }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunTrackedAsync(async () => { await InitializeCoreAsync(cancellationToken).ConfigureAwait(false); return true; });

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await OpenAsync(cancellationToken);
            var existing = await ScalarAsync(db, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'", cancellationToken);
            if (existing != 0)
            {
                await ValidateExistingAsync(db, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var transaction = db.BeginTransaction();
                await using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = Schema + "\nINSERT INTO SchemaMigration(Version, Checksum, AppliedAtUtc) VALUES(1, 'registry-v1', $now);";
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await ScalarAsync(db, "SELECT MAX(Version) FROM SchemaMigration", cancellationToken).ConfigureAwait(false) == 1)
            {
                await using var transaction = db.BeginTransaction();
                await using var migration = db.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = OperationsMigration.Sql + "\nINSERT INTO SchemaMigration VALUES(2,'operations-v2',$now);";
                migration.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await ScalarAsync(db, "SELECT MAX(Version) FROM SchemaMigration", cancellationToken).ConfigureAwait(false) == 2)
            {
                await using var transaction = db.BeginTransaction();
                await using var migration = db.CreateCommand(); migration.Transaction = transaction;
                migration.CommandText = MaintenanceMigration.Sql + "\nINSERT INTO SchemaMigration VALUES(3,'maintenance-v3',$now);";
                migration.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(db, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
        RunTrackedAsync(() => ReadCoreAsync(action, cancellationToken));

    private async Task<T> ReadCoreAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        return await action(db, cancellationToken);
    }

    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
        RunTrackedAsync(() => WriteCoreAsync(action, cancellationToken));

    private async Task<T> WriteCoreAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(cancellationToken);
            try
            {
                var value = await action(db, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return value;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _writeGate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            await db.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(db, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection db, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private Task<T> RunTrackedAsync<T>(Func<Task<T>> action)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _activeOperations++;
        }
        return CompleteTrackedAsync(action);
    }

    private async Task<T> CompleteTrackedAsync<T>(Func<Task<T>> action)
    {
        try { return await Task.Run(action).ConfigureAwait(false); }
        finally
        {
            lock (_lifecycle)
            {
                if (--_activeOperations == 0 && _disposeTask is not null) _drained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is null)
            {
                if (_activeOperations == 0) _drained.TrySetResult();
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private static async Task ValidateExistingAsync(SqliteConnection db, CancellationToken ct)
    {
        if (await ScalarAsync(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaMigration'", ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Database is not a MoneyCounter database.");
        try
        {
            if (await ScalarAsync(db, "SELECT COUNT(*) FROM SchemaMigration WHERE Version=1 AND Checksum='registry-v1'", ct).ConfigureAwait(false) != 1 ||
                await ScalarAsync(db, "SELECT COUNT(*) FROM SchemaMigration WHERE NOT ((Version=1 AND Checksum='registry-v1') OR (Version=2 AND Checksum='operations-v2') OR (Version=3 AND Checksum='maintenance-v3'))", ct).ConfigureAwait(false) != 0 ||
                await ScalarAsync(db, "SELECT COUNT(*) FROM SchemaMigration", ct).ConfigureAwait(false) != await ScalarAsync(db, "SELECT MAX(Version) FROM SchemaMigration", ct).ConfigureAwait(false))
                throw new InvalidOperationException("Database schema is unsupported.");
            // Preparing explicit projections rejects missing tables/columns without repairing user data.
            const string projections = """
                SELECT Version,Checksum,AppliedAtUtc FROM SchemaMigration LIMIT 0;
                SELECT Id FROM ImportBatch LIMIT 0;
                SELECT Id FROM SimulationDataset LIMIT 0;
                SELECT Id,Manufacturer,ModelName,RatedCountLife,Notes,IsActive,Revision,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc,UpdatedAtUtc FROM Model LIMIT 0;
                SELECT Id,AssetCode,ModelId,CommissionedOn,PurchasedOn,Location,ResponsiblePerson,Notes,IsActive,Revision,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc,UpdatedAtUtc FROM Device LIMIT 0;
                SELECT OperationId,PayloadHash,ResultKind,ResultId,ResultJson,CreatedAtUtc FROM OperationReceipt LIMIT 0;
                """;
            foreach (var projection in projections.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var command = db.CreateCommand();
                command.CommandText = projection;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            }
            if (await ScalarAsync(db, "SELECT MAX(Version) FROM SchemaMigration", ct).ConfigureAwait(false) >= 2)
            {
                foreach (var projection in new[] {
                    "SELECT Id,DeviceId,RecordedAt,Status,CumulativeCount,Notes,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc FROM StatusRecord LIMIT 0",
                    "SELECT Id,DeviceId,DiscoveredAt,Description,Status,HandlingNotes,ClosedAt,Revision,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc FROM Anomaly LIMIT 0" })
                {
                    await using var command = db.CreateCommand();
                    command.CommandText = projection;
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                }
            }
            if (await ScalarAsync(db, "SELECT MAX(Version) FROM SchemaMigration", ct).ConfigureAwait(false) >= 3)
            {
                foreach (var projection in new[] {
                    "SELECT Id,FaultNo,DeviceId,SourceAnomalyId,RegisteredAt,FaultType,Severity,Description,Status,StartedAt,ClosedAt,FinalResult,Revision,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc FROM Fault LIMIT 0",
                    "SELECT Id,FaultId,RepairedAt,Action,Result,Technician,Notes,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc FROM Repair LIMIT 0" })
                {
                    await using var command = db.CreateCommand(); command.CommandText = projection;
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            throw new InvalidOperationException("Database schema is incomplete or unsupported.", ex);
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS SchemaMigration (Version INTEGER PRIMARY KEY, Checksum TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ImportBatch (Id INTEGER PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS SimulationDataset (Id INTEGER PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS Model (
          Id INTEGER PRIMARY KEY, Manufacturer TEXT NOT NULL, ModelName TEXT NOT NULL, RatedCountLife INTEGER NULL CHECK(RatedCountLife IS NULL OR RatedCountLife > 0), Notes TEXT NOT NULL DEFAULT '',
          IsActive INTEGER NOT NULL CHECK(IsActive IN (0,1)), Revision INTEGER NOT NULL CHECK(Revision >= 0), Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')),
          ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER NULL REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)),
          UNIQUE(Manufacturer, ModelName));
        CREATE TABLE IF NOT EXISTS Device (
          Id INTEGER PRIMARY KEY, AssetCode TEXT NOT NULL UNIQUE, ModelId INTEGER NOT NULL REFERENCES Model(Id), CommissionedOn TEXT NULL, PurchasedOn TEXT NULL,
          Location TEXT NOT NULL DEFAULT '', ResponsiblePerson TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '', IsActive INTEGER NOT NULL CHECK(IsActive IN (0,1)),
          Revision INTEGER NOT NULL CHECK(Revision >= 0), Source TEXT NOT NULL CHECK(Source IN ('MANUAL','CSV','SIMULATED')),
          ImportBatchId INTEGER NULL REFERENCES ImportBatch(Id), SimulationDatasetId INTEGER NULL REFERENCES SimulationDataset(Id), CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
          CHECK((Source='MANUAL' AND ImportBatchId IS NULL AND SimulationDatasetId IS NULL) OR (Source='CSV' AND ImportBatchId IS NOT NULL AND SimulationDatasetId IS NULL) OR (Source='SIMULATED' AND ImportBatchId IS NULL AND SimulationDatasetId IS NOT NULL)));
        CREATE TABLE IF NOT EXISTS OperationReceipt (OperationId TEXT PRIMARY KEY, PayloadHash TEXT NOT NULL, ResultKind TEXT NOT NULL, ResultId INTEGER NOT NULL, ResultJson TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL);
        """;
}
