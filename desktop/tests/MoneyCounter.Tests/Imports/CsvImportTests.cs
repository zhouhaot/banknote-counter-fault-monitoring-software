using System.Text;
using MoneyCounter.Core.Imports;
using MoneyCounter.Infrastructure.Imports;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Imports;

public sealed class CsvImportTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N"));
    private DbStore _store = null!;
    private SqliteCsvImportService _service = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync() { Directory.CreateDirectory(_directory); _store = new(Path.Combine(_directory, "test.db")); await _store.InitializeAsync(Ct); _service = new(_store); }
    public async ValueTask DisposeAsync() { await _store.DisposeAsync(); Directory.Delete(_directory, true); }
    private async Task<string> FileAsync(ImportKind kind, string content) { var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".csv"); await File.WriteAllTextAsync(path, _service.GetTemplate(kind) + content, new UTF8Encoding(false), Ct); return path; }
    private async Task<CsvPreview> Preview(ImportKind kind, string path) { var p = await _service.PreviewAsync(kind, path, Ct); Assert.True(p.IsSuccess, p.Error?.Message); return p.Value!; }
    private async Task<CsvCommitResult> Commit(ImportKind kind, string path, Guid? operation = null) { var p = await Preview(kind, path); var r = await _service.CommitAsync(operation ?? Guid.NewGuid(), kind, path, p.Sha256, Ct); Assert.True(r.IsSuccess, r.Error?.Message); return r.Value!; }
    private async Task SeedDevice() { Assert.True((await Commit(ImportKind.Model, await FileAsync(ImportKind.Model, "厂商,型号,1000,\r\n"))).IsCommitted); Assert.True((await Commit(ImportKind.Device, await FileAsync(ImportKind.Device, "A,厂商,型号,2026-01-01,,大厅,张三,\r\n"))).IsCommitted); }
    private Task<long> Count(string table) => _store.ReadAsync(async (db, ct) => { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }, Ct);

    [Fact]
    public async Task QuotedMultilineAndBomRoundTripWithReceipt()
    {
        var path = await FileAsync(ImportKind.Model, "厂商,型号,9223372036854775807,\"两行\r\n含\"\"引号\"\",逗号\"\r\n");
        await File.WriteAllTextAsync(path, "\uFEFF" + await File.ReadAllTextAsync(path, Ct), new UTF8Encoding(false), Ct);
        var op = Guid.NewGuid(); var first = await Commit(ImportKind.Model, path, op);
        var hash = (await Preview(ImportKind.Model, path)).Sha256;
        var second = await _service.CommitAsync(op, ImportKind.Model, path, hash, Ct);
        Assert.True(first.IsCommitted); Assert.Equal(first.Batch, second.Value!.Batch); Assert.Equal(1, await Count("Model"));
        var history = await _service.ListHistoryAsync(ct: Ct); Assert.Single(history.Value!.Items);
    }

    [Theory]
    [InlineData("厂商,型号,1,\"未闭合")]
    [InlineData("厂商,型\"号,1,备注")]
    [InlineData("厂商,型号,1,\"文本\"非法")]
    public async Task MalformedQuotesRejectAll(string row)
    { var path = await FileAsync(ImportKind.Model, row); Assert.Contains((await Preview(ImportKind.Model, path)).Errors, x => x.Code == "MALFORMED_CSV"); Assert.False((await Commit(ImportKind.Model, path)).IsCommitted); Assert.Equal(0, await Count("Model")); }

    [Fact]
    public async Task StrictEncodingHeaderRawCellAndRecordLimits()
    {
        var path = await FileAsync(ImportKind.Model, ""); await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0x41], Ct);
        Assert.Contains((await Preview(ImportKind.Model, path)).Errors, x => x.Code == "INVALID_ENCODING");
        await File.WriteAllTextAsync(path, "model_name,manufacturer,rated_count_life,notes\r\n", Ct); Assert.Contains((await Preview(ImportKind.Model, path)).Errors, x => x.Code == "INVALID_HEADER");
        path = await FileAsync(ImportKind.Model, "厂商,型号,1," + new string(' ', 2001)); Assert.Contains((await Preview(ImportKind.Model, path)).Errors, x => x.Code == "CELL_TOO_LONG");
        path = await FileAsync(ImportKind.Model, string.Concat(Enumerable.Range(0, 10001).Select(i => $"厂商,型号{i},1,\r\n"))); Assert.Contains((await Preview(ImportKind.Model, path)).Errors, x => x.Code == "ROW_LIMIT_EXCEEDED");
        await File.WriteAllBytesAsync(path, new byte[5 * 1024 * 1024 + 1], Ct); Assert.False((await _service.PreviewAsync(ImportKind.Model, path, Ct)).IsSuccess);
    }

    [Fact]
    public async Task DuplicateRowsAndDatabaseChangeAreAtomic()
    {
        var path = await FileAsync(ImportKind.Model, "厂商,型号,1,\r\n厂商,型号,2,\r\n"); Assert.False((await Commit(ImportKind.Model, path)).IsCommitted); Assert.Equal(0, await Count("ImportBatch"));
        path = await FileAsync(ImportKind.Model, "厂商,型号,1,\r\n厂商,另一个,2,\r\n"); var preview = await Preview(ImportKind.Model, path); Assert.True(preview.CanCommit);
        Assert.True((await Commit(ImportKind.Model, await FileAsync(ImportKind.Model, "厂商,型号,1,\r\n"))).IsCommitted);
        var result = await _service.CommitAsync(Guid.NewGuid(), ImportKind.Model, path, preview.Sha256, Ct);
        Assert.False(result.Value!.IsCommitted); Assert.Contains(result.Value.Errors, x => x.Code == "DUPLICATE_DATABASE"); Assert.Equal(1, await Count("Model")); Assert.Equal(1, await Count("ImportBatch"));
    }

    [Fact]
    public async Task ChangedFileRequiresNewPreview()
    { var path = await FileAsync(ImportKind.Model, "厂商,型号,1,"); var p = await Preview(ImportKind.Model, path); await File.AppendAllTextAsync(path, "修改", Ct); Assert.False((await _service.CommitAsync(Guid.NewGuid(), ImportKind.Model, path, p.Sha256, Ct)).IsSuccess); Assert.Equal(0, await Count("Model")); }

    [Fact]
    public async Task StatusChecksFullTimelineAndExactUtcIdentity()
    {
        await SeedDevice();
        Assert.True((await Commit(ImportKind.Status, await FileAsync(ImportKind.Status, "A,2026-01-01T08:00:00+08:00,RUNNING,100,\r\nA,2026-01-01T10:00:00+08:00,RUNNING,300,\r\n"))).IsCommitted);
        var path = await FileAsync(ImportKind.Status, "A,2026-01-01T01:00:00Z,RUNNING,301,\r\n"); Assert.Contains((await Preview(ImportKind.Status, path)).Errors, x => x.Code == "MONOTONICITY");
        path = await FileAsync(ImportKind.Status, "A,2026-01-01T00:00:00Z,RUNNING,100,\r\n"); Assert.Contains((await Preview(ImportKind.Status, path)).Errors, x => x.Code == "DUPLICATE_DATABASE");
        path = await FileAsync(ImportKind.Status, "A,2026-01-01T01:00:00.0000001Z,RUNNING,200,\r\nA,2026-01-01T01:00:00.0000002Z,RUNNING,201,\r\n"); Assert.True((await Commit(ImportKind.Status, path)).IsCommitted); Assert.Equal(4, await Count("StatusRecord"));
        path = await FileAsync(ImportKind.Status, "A,2026-01-01T03:00:00,RUNNING,400,\r\n"); Assert.Contains((await Preview(ImportKind.Status, path)).Errors, x => x.Code == "INVALID_DATETIME");
    }

    [Fact]
    public async Task SimulatedParentAndMissingDependencyRejected()
    {
        await _store.WriteAsync(async (db, tx, ct) => { using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO SimulationDataset(Id) VALUES(123); INSERT INTO Model(Manufacturer,ModelName,IsActive,Revision,Source,SimulationDatasetId,CreatedAtUtc,UpdatedAtUtc) VALUES('模拟','型号',1,0,'SIMULATED',123,'2026','2026')"; await c.ExecuteNonQueryAsync(ct); return true; }, Ct);
        var path = await FileAsync(ImportKind.Device, "A,模拟,型号,,,,,\r\nB,不存在,型号,,,,,\r\n"); var preview = await Preview(ImportKind.Device, path);
        Assert.Contains(preview.Errors, x => x.Code == "SOURCE_OR_INACTIVE"); Assert.Contains(preview.Errors, x => x.Code == "MISSING_DEPENDENCY"); Assert.False((await Commit(ImportKind.Device, path)).IsCommitted); Assert.Equal(0, await Count("Device"));
    }

    [Fact]
    public async Task CancelledCommitRollsBackAndErrorsAreSpreadsheetSafe()
    {
        var path = await FileAsync(ImportKind.Model, "厂商,型号,1,"); var p = await Preview(ImportKind.Model, path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CommitAsync(Guid.NewGuid(), ImportKind.Model, path, p.Sha256, cancelled.Token)); Assert.Equal(0, await Count("ImportBatch"));
        string csv = _service.GetErrorCsv([new(2, "=SUM(1)", "+CMD", "@evil")]); Assert.Contains("\"'=SUM(1)\"", csv); Assert.Contains("\"'+CMD\"", csv); Assert.Contains("\"'@evil\"", csv);
    }

    [Fact]
    public async Task MidTransactionFailureRollsBackAllRecordsAndBatch()
    {
        await _store.WriteAsync(async (db, tx, ct) => { using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = "CREATE TRIGGER TestAbort BEFORE INSERT ON Model WHEN NEW.ModelName='失败' BEGIN SELECT RAISE(ABORT,'injected'); END"; await c.ExecuteNonQueryAsync(ct); return true; }, Ct);
        var path = await FileAsync(ImportKind.Model, "厂商,成功,1,\r\n厂商,失败,1,\r\n"); var p = await Preview(ImportKind.Model, path);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _service.CommitAsync(Guid.NewGuid(), ImportKind.Model, path, p.Sha256, Ct)); Assert.Equal(0, await Count("Model")); Assert.Equal(0, await Count("ImportBatch")); Assert.Equal(0, await Count("OperationReceipt"));
    }
    [Fact]
    public async Task CommittedRetryUsesReceiptEvenWhenOriginalFileIsMissingOrChanged()
    {
        var path = await FileAsync(ImportKind.Model, "厂商,型号,1,");
        var preview = await Preview(ImportKind.Model, path); var op = Guid.NewGuid();
        var first = await _service.CommitAsync(op, ImportKind.Model, path, preview.Sha256, Ct);
        Assert.True(first.Value!.IsCommitted);
        await File.WriteAllTextAsync(path, "invalid csv", Ct);
        var changed = await _service.CommitAsync(op, ImportKind.Model, path, preview.Sha256.ToLowerInvariant(), Ct);
        Assert.Equal(first.Value.Batch, changed.Value!.Batch);
        File.Delete(path);
        var missing = await _service.CommitAsync(op, ImportKind.Model, path, preview.Sha256, Ct);
        Assert.Equal(first.Value.Batch, missing.Value!.Batch);
        Assert.False((await _service.CommitAsync(op, ImportKind.Device, path, preview.Sha256, Ct)).IsSuccess);
        Assert.False((await _service.CommitAsync(op, ImportKind.Model, path, new string('0',64), Ct)).IsSuccess);
        Assert.Equal(1, await Count("Model")); Assert.Equal(1, await Count("ImportBatch"));
    }

    [Fact]
    public async Task ConcurrentRetriesCommitExactlyOneBatch()
    {
        var path = await FileAsync(ImportKind.Model, "厂商,型号,1,"); var preview = await Preview(ImportKind.Model, path); var op = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _service.CommitAsync(op, ImportKind.Model, path, preview.Sha256, Ct)));
        Assert.All(results, result => Assert.True(result.Value!.IsCommitted));
        Assert.Single(results.Select(result => result.Value!.Batch!.Id).Distinct());
        Assert.Equal(1, await Count("Model")); Assert.Equal(1, await Count("ImportBatch"));
    }
    [Fact]
    public async Task CancellationAfterUncommittedWalWritesRollsBackEntireImport()
    {
        // More than SQLite's default page cache forces uncommitted WAL frames to disk.
        // The final trigger keeps the transaction open long enough to observe that evidence.
        await _store.WriteAsync(async (db, tx, ct) =>
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "CREATE TRIGGER TestSlowLast BEFORE INSERT ON Model WHEN NEW.ModelName='型号1999' BEGIN SELECT sum(n) FROM (WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<2000000) SELECT n FROM numbers); END";
            return await command.ExecuteNonQueryAsync(ct);
        }, Ct);
        var path = await FileAsync(ImportKind.Model, string.Concat(Enumerable.Range(0, 2000).Select(i => $"厂商,型号{i},1,{new string('a', 1900)}\r\n")));
        var preview = await Preview(ImportKind.Model, path); Assert.True(preview.CanCommit);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var commit = _service.CommitAsync(Guid.NewGuid(), ImportKind.Model, path, preview.Sha256, cancellation.Token);
        var wal = Path.Combine(_directory, "test.db-wal");
        bool sawUncommittedWrite = false;
        while (!commit.IsCompleted)
        {
            if (File.Exists(wal) && new FileInfo(wal).Length > 32)
            {
                sawUncommittedWrite = !commit.IsCompleted;
                cancellation.Cancel(); break;
            }
            await Task.Delay(1, Ct);
        }
        Assert.True(sawUncommittedWrite, "Expected cancellation while imported pages were uncommitted in the WAL.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        Assert.Equal(0, await Count("Model")); Assert.Equal(0, await Count("ImportBatch")); Assert.Equal(0, await Count("OperationReceipt"));
    }}


