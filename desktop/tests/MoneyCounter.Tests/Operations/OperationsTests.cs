using MoneyCounter.Core.Operations;
using MoneyCounter.Infrastructure.Operations;
using MoneyCounter.Infrastructure.Storage;
using MoneyCounter.Core.Registry;
namespace MoneyCounter.Tests.Operations;

public sealed class OperationsTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"operations-{Guid.NewGuid():N}.db");
    private DbStore store = null!;
    private IOperationsService service = null!;
    private long device;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly DateTimeOffset time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public async ValueTask InitializeAsync()
    {
        store = new(path); await store.InitializeAsync(Ct);
        var registry = new SqliteRegistryService(store);
        var model = (await registry.CreateModelAsync(new(Guid.NewGuid(), "测试", "型号", null, ""), Ct)).Value!;
        device = (await registry.CreateDeviceAsync(new(Guid.NewGuid(), "设备", model.Id, null, null, "", "", ""), Ct)).Value!.Id;
        service = new SqliteOperationsService(store);
    }
    public async ValueTask DisposeAsync() { await store.DisposeAsync(); File.Delete(path); }
    private Task<MoneyCounter.Core.Result<StatusDetail>> Add(int hour, long count) => service.RecordStatusAsync(new(Guid.NewGuid(), device, time.AddHours(hour), "RUNNING", count, ""), Ct);
    [Fact]
    public async Task HistoricalInsertChecksBothNeighborsAndRecomputesDeltas()
    {
        Assert.True((await Add(0, 10)).IsSuccess); Assert.True((await Add(2, 30)).IsSuccess);
        Assert.Equal(MoneyCounter.Core.ErrorCodes.MonotonicityViolation, (await Add(1, 9)).Error!.Code); Assert.Equal(MoneyCounter.Core.ErrorCodes.MonotonicityViolation, (await Add(1, 31)).Error!.Code);
        Assert.True((await Add(1, 20)).IsSuccess);
        var rows = (await service.ListStatusesAsync(new(DeviceId: device), Ct)).Value!;
        Assert.Equal(3, rows.TotalCount); Assert.Equal(10, rows.Items[0].Increment); Assert.Null(rows.Items[2].Increment);
    }
    [Fact]
    public async Task DuplicateTimeAndNegativeCountNeverWrite()
    {
        Assert.True((await Add(0, 0)).IsSuccess); Assert.False((await Add(0, 1)).IsSuccess); Assert.False((await Add(1, -1)).IsSuccess);
        Assert.Equal(1, (await service.ListStatusesAsync(new(), Ct)).Value!.TotalCount);
    }
    [Fact]
    public async Task Int64BoundaryAndReceiptAreExact()
    {
        await Add(0, 0);
        var command = new RecordStatusCommand(Guid.NewGuid(), device, time.AddHours(1), "RETIRED", long.MaxValue, "");
        var first = await service.RecordStatusAsync(command, Ct); var retry = await service.RecordStatusAsync(command, Ct);
        Assert.Equal(long.MaxValue, first.Value!.Increment); Assert.Equal(first.Value, retry.Value);
        Assert.False((await service.RecordStatusAsync(command with { CumulativeCount = 1 }, Ct)).IsSuccess);
    }
    [Fact]
    public async Task AnomalyCloseChecksTimeRevisionAndStateWithOriginalReceipt()
    {
        var command = new CreateAnomalyCommand(Guid.NewGuid(), device, time, "卡钞");
        var anomaly = (await service.CreateAnomalyAsync(command, Ct)).Value!;
        Assert.False((await service.CloseAnomalyAsync(new(Guid.NewGuid(), anomaly.Id, 0, time.AddSeconds(-1), "检查"), Ct)).IsSuccess);
        Assert.False((await service.CloseAnomalyAsync(new(Guid.NewGuid(), anomaly.Id, 9, time, "检查"), Ct)).IsSuccess);
        var close = new CloseAnomalyCommand(Guid.NewGuid(), anomaly.Id, 0, time, "检查完成");
        Assert.True((await service.CloseAnomalyAsync(close, Ct)).IsSuccess);
        Assert.True((await service.CloseAnomalyAsync(close, Ct)).IsSuccess);
        Assert.False((await service.CloseAnomalyAsync(close with { OperationId = Guid.NewGuid(), ExpectedRevision = 1 }, Ct)).IsSuccess);
        Assert.Equal("OPEN", (await service.CreateAnomalyAsync(command, Ct)).Value!.Status);
    }
    [Fact]
    public async Task UnknownAndSimulatedDevicesRejectManualRecords()
    {
        Assert.False((await service.RecordStatusAsync(new(Guid.NewGuid(), 999, time, "RUNNING", 1, ""), Ct)).IsSuccess);
        await store.WriteAsync(async (db, tx, ct) => { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO SimulationDataset(Id) VALUES(1); UPDATE Model SET Source='SIMULATED',SimulationDatasetId=1 WHERE Id=(SELECT ModelId FROM Device WHERE Id=$id); UPDATE Device SET Source='SIMULATED',SimulationDatasetId=1 WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", device); return await cmd.ExecuteNonQueryAsync(ct); }, Ct);
        Assert.Equal(MoneyCounter.Core.ErrorCodes.SourceMismatch, (await Add(0, 1)).Error!.Code);
        Assert.False((await service.CreateAnomalyAsync(new(Guid.NewGuid(), device, time, "异常"), Ct)).IsSuccess);
    }
    [Fact]
    public async Task PaginationKeepsBaselineOutsideDateWindowAndOffsetDuplicatesCollide()
    {
        await Add(0, 10); await Add(1, 20); await Add(2, 40);
        var page = (await service.ListStatusesAsync(new(PageSize: 1, DeviceId: device, From: time.AddHours(1)), Ct)).Value!;
        Assert.Equal(2, page.TotalCount); Assert.Equal(20, page.Items[0].Increment);
        Assert.False((await service.RecordStatusAsync(new(Guid.NewGuid(), device, time.ToOffset(TimeSpan.FromHours(8)), "RUNNING", 10, ""), Ct)).IsSuccess);
        Assert.False((await service.ListStatusesAsync(new(Page: 0), Ct)).IsSuccess);
        Assert.False((await service.ListAnomaliesAsync(new(Status: "INVALID"), Ct)).IsSuccess);
    }
    [Fact]
    public async Task SimultaneousDuplicateCommandsProduceOneRecord()
    {
        var command = new RecordStatusCommand(Guid.NewGuid(), device, time, "STOPPED", 0, "");
        var results = await Task.WhenAll(service.RecordStatusAsync(command, Ct), service.RecordStatusAsync(command, Ct));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Value, results[1].Value);
        Assert.Equal(1, (await service.ListStatusesAsync(new(), Ct)).Value!.TotalCount);
    }
    [Fact]
    public async Task EmptyHistoryIsUnknownAndInvalidStatesNeverWrite()
    {
        Assert.Null((await service.LatestStatusAsync(device, Ct)).Value);
        Assert.False((await service.RecordStatusAsync(new(Guid.NewGuid(), device, time, "ONLINE", 0, ""), Ct)).IsSuccess);
        Assert.False((await service.CreateAnomalyAsync(new(Guid.NewGuid(), device, time, "  "), Ct)).IsSuccess);
        Assert.Equal(0, (await service.ListAnomaliesAsync(new(), Ct)).Value!.TotalCount);
    }
}
