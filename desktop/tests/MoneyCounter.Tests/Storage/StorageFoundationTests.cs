using Microsoft.Data.Sqlite;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Storage;

public sealed class StorageFoundationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionOneUpgradePreservesRegistryAndRollsBackFailedMigration(bool conflict)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using (var setup = new DbStore(path))
            {
                await setup.InitializeAsync(ct);
                var registry = new SqliteRegistryService(setup);
                Assert.True((await registry.CreateModelAsync(new(Guid.NewGuid(), "迁移厂商", "保留型号", null, "原始备注"), ct)).IsSuccess);
                await setup.WriteAsync(async (db, tx, token) =>
                {
                    using var command = db.CreateCommand(); command.Transaction = tx;
                    command.CommandText = "DROP TABLE InventoryMovement; DROP TABLE Consumable; DROP TABLE Repair; DROP TABLE Fault; DROP TABLE StatusRecord; DROP TABLE Anomaly; DELETE FROM SchemaMigration WHERE Version>=2;" + (conflict ? "CREATE VIEW Anomaly AS SELECT 1 AS Id;" : "");
                    return await command.ExecuteNonQueryAsync(token);
                }, ct);
            }
            await using var upgraded = new DbStore(path);
            if (conflict) await Assert.ThrowsAsync<SqliteException>(() => upgraded.InitializeAsync(ct));
            else await upgraded.InitializeAsync(ct);
            Assert.Equal(1L, await upgraded.ReadAsync((db, token) => ScalarAsync(db, "SELECT COUNT(*) FROM Model WHERE Notes='原始备注'", token), ct));
            Assert.Equal(conflict ? 1L : 4L, await upgraded.ReadAsync((db, token) => ScalarAsync(db, "SELECT COUNT(*) FROM SchemaMigration", token), ct));
            Assert.Equal(conflict ? 0L : 1L, await upgraded.ReadAsync((db, token) => ScalarAsync(db, "SELECT COUNT(*) FROM sqlite_master WHERE name='StatusRecord'", token), ct));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("INSERT INTO SchemaMigration VALUES(2,'future','now');")]
    [InlineData("")]
    [InlineData("INSERT INTO SchemaMigration VALUES(1,'registry-v1','now');")]
    public async Task RejectedSchemaDoesNotChangeDatabase(string migration)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE SchemaMigration(Version INTEGER PRIMARY KEY,Checksum TEXT,AppliedAtUtc TEXT);" + migration;
                await command.ExecuteNonQueryAsync(ct);
            }
            var journal = await ScalarObjectAsync(connection, "PRAGMA journal_mode", ct);
            await using var store = new DbStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(ct));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'", ct));
            Assert.Equal(journal, await ScalarObjectAsync(connection, "PRAGMA journal_mode", ct));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExistingViewOnlyDatabaseIsRejectedWithoutAddingTables()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE VIEW Device AS SELECT 1 AS Id;";
                await command.ExecuteNonQueryAsync(ct);
            }
            await using var store = new DbStore(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(ct));
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'", ct));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DisposeDrainsAcceptedReadsAndWritesAndRejectsNewOperations()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        var store = new DbStore(path);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await store.InitializeAsync(ct);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = store.WriteAsync(async (_, _, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return 1;
            }, ct);
            await entered.Task.WaitAsync(ct);
            var queued = store.WriteAsync((_, _, _) => Task.FromResult(2), ct);
            var read = store.ReadAsync(async (_, token) => { await release.Task.WaitAsync(token); return 3; }, ct);
            var disposing = store.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = store.ReadAsync((_, _) => Task.FromResult(0), ct); });
            Assert.Throws<ObjectDisposedException>(() => { _ = store.InitializeAsync(ct); });
            release.SetResult();
            Assert.Equal(new[] { 1, 2, 3 }, await Task.WhenAll(first, queued, read));
            await disposing.WaitAsync(ct);
            await store.DisposeAsync();
        }
        finally
        {
            release.TrySetResult();
            await store.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedOpenDoesNotPreventDisposalOrSubsequentUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"money-counter-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "data.db");
        await using var store = new DbStore(path);
        await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync(ct));
        Directory.CreateDirectory(directory);
        try { await store.InitializeAsync(ct); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task MigrationIsIdempotentAndEnablesRequiredPragmas()
    {
        var path = TempPath();
        await using (var first = new DbStore(path)) await first.InitializeAsync(TestContext.Current.CancellationToken);
        await using (var second = new DbStore(path)) await second.InitializeAsync(TestContext.Current.CancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={path};Foreign Keys=True;Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys", TestContext.Current.CancellationToken));
            Assert.Equal("wal", ((string)(await ScalarObjectAsync(connection, "PRAGMA journal_mode", TestContext.Current.CancellationToken))!).ToLowerInvariant());
            Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM SchemaMigration", TestContext.Current.CancellationToken));
        }
        File.Delete(path);
    }

    [Fact]
    public async Task DatabaseRejectsInvalidSourceCombinationAndRollsBackTransaction()
    {
        var path = TempPath();
        await using var store = new DbStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => store.WriteAsync<object?>(async (db, tx, ct) =>
        {
            await using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO Model(Manufacturer,ModelName,IsActive,Revision,Source,CreatedAtUtc,UpdatedAtUtc) VALUES('A','M',1,0,'CSV',$now,$now)";
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            return null;
        }, TestContext.Current.CancellationToken));

        Assert.Equal(0L, await store.ReadAsync((db, ct) => ScalarAsync(db, "SELECT COUNT(*) FROM Model", ct), TestContext.Current.CancellationToken));
        File.Delete(path);
    }

    [Fact]
    public async Task ExistingNonProductDatabaseIsRejected()
    {
        var path = TempPath();
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ForeignApplicationData(Id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var store = new DbStore(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(TestContext.Current.CancellationToken));
        File.Delete(path);
    }

    [Fact]
    public async Task ParallelWritesAreSerialized()
    {
        var path = TempPath();
        await using var store = new DbStore(path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var active = 0;
        var maximum = 0;

        var jobs = Enumerable.Range(0, 12).Select(i => store.WriteAsync(async (_, _, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, now);
            await Task.Delay(10, ct);
            Interlocked.Decrement(ref active);
            return i;
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(jobs);

        Assert.Equal(1, maximum);
        File.Delete(path);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"money-counter-{Guid.NewGuid():N}.db");
    private static async Task<long> ScalarAsync(SqliteConnection db, string sql, CancellationToken ct = default) => Convert.ToInt64(await ScalarObjectAsync(db, sql, ct));
    private static async Task<object?> ScalarObjectAsync(SqliteConnection db, string sql, CancellationToken ct = default)
    {
        await using var command = db.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(ct);
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        int current;
        do { current = target; if (current >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
