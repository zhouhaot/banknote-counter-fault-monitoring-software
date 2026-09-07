using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Core.Operations;
using MoneyCounter.Core.Registry;
using MoneyCounter.Infrastructure.Maintenance;
using MoneyCounter.Infrastructure.Operations;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Maintenance;
public sealed class MaintenanceTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"maintenance-{Guid.NewGuid():N}.db");
    private DbStore store = null!;
    private SqliteMaintenanceService service = null!;
    private long device;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Time = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    public async ValueTask InitializeAsync()
    {
        store = new(path); await store.InitializeAsync(Ct);
        var registry = new SqliteRegistryService(store);
        var model = (await registry.CreateModelAsync(new(Guid.NewGuid(), "Test", "M", null, ""), Ct)).Value!;
        device = (await registry.CreateDeviceAsync(new(Guid.NewGuid(), "D", model.Id, null, null, "", "", ""), Ct)).Value!.Id;
        service = new(store);
    }
    public async ValueTask DisposeAsync() { await store.DisposeAsync(); File.Delete(path); }
    private async Task<FaultDetail> Create() => (await service.CreateFaultAsync(new(Guid.NewGuid(), device, Time, "CARD_JAM", "HIGH", "卡钞"), Ct)).Value!;
    [Fact] public async Task ClosingRequiresRepairAndOrderedTimesAndFreezesRepair()
    {
        var f = await Create();
        Assert.False((await service.CloseFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time, "完成"), Ct)).IsSuccess);
        f = (await service.StartFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(1)), Ct)).Value!;
        Assert.False((await service.CloseFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(3), "完成"), Ct)).IsSuccess);
        f = (await service.AddRepairAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(2), "清理", "正常", "甲", ""), Ct)).Value!;
        Assert.False((await service.CloseFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(1), "完成"), Ct)).IsSuccess);
        var noResult = await service.CloseFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(3), "  "), Ct);
        Assert.Equal("FinalResult", noResult.Error!.Field);
        f = (await service.CloseFaultAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(3), "完成"), Ct)).Value!;
        Assert.Equal(ErrorCodes.InvalidTransition, (await service.AddRepairAsync(new(Guid.NewGuid(), f.Id, f.Revision, Time.AddMinutes(4), "检查", "正常", "甲", ""), Ct)).Error!.Code);
        var r = (await service.ListRepairsAsync(f.Id, cancellationToken: Ct)).Value!.Items.Single();
        Assert.False((await service.DeleteRepairAsync(new(Guid.NewGuid(), r.Id, f.Revision), Ct)).IsSuccess);
        Assert.False((await service.UpdateRepairAsync(new(Guid.NewGuid(), r.Id, f.Revision, Time.AddMinutes(2), "改", "改", "", ""), Ct)).IsSuccess);
    }
    [Fact] public async Task ConversionIsAtomicAndReplayDoesNotCreateAnotherFault()
    {
        var a = (await new SqliteOperationsService(store).CreateAnomalyAsync(new(Guid.NewGuid(), device, Time, "异常"), Ct)).Value!;
        var cmd = new ConvertAnomalyCommand(Guid.NewGuid(), a.Id, a.Revision, Time, "CARD_JAM", "HIGH", "卡钞");
        var first = await service.ConvertAnomalyAsync(cmd, Ct); var replay = await service.ConvertAnomalyAsync(cmd, Ct);
        Assert.True(first.IsSuccess); Assert.Equal(first.Value, replay.Value);
        Assert.False((await service.ConvertAnomalyAsync(cmd with { OperationId = Guid.NewGuid() }, Ct)).IsSuccess);
        Assert.Equal(1, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
        Assert.Equal("CONVERTED", (await new SqliteOperationsService(store).GetAnomalyAsync(a.Id, Ct)).Value!.Status);
    }
    [Fact] public async Task FailedConversionLeavesAnomalyOpen()
    {
        var a = (await new SqliteOperationsService(store).CreateAnomalyAsync(new(Guid.NewGuid(), device, Time, "异常"), Ct)).Value!;
        Assert.False((await service.ConvertAnomalyAsync(new(Guid.NewGuid(), a.Id, a.Revision, Time.AddMinutes(-1), "OTHER", "LOW", "异常"), Ct)).IsSuccess);
        Assert.Equal("OPEN", (await new SqliteOperationsService(store).GetAnomalyAsync(a.Id, Ct)).Value!.Status);
    }
    [Fact] public async Task RepairEditUsesAggregateRevisionAndReplay()
    {
        var f = await Create(); var cmd = new AddRepairCommand(Guid.NewGuid(), f.Id, f.Revision, Time, "检查", "待定", "", "");
        f = (await service.AddRepairAsync(cmd, Ct)).Value!; Assert.Equal(f, (await service.AddRepairAsync(cmd, Ct)).Value);
        var r = (await service.ListRepairsAsync(f.Id, cancellationToken: Ct)).Value!.Items.Single();
        Assert.False((await service.DeleteRepairAsync(new(Guid.NewGuid(), r.Id, 0), Ct)).IsSuccess);
        f = (await service.UpdateRepairAsync(new(Guid.NewGuid(), r.Id, f.Revision, Time, "清理", "正常", "甲", ""), Ct)).Value!;
        Assert.Equal("正常", (await service.ListRepairsAsync(f.Id, cancellationToken: Ct)).Value!.Items.Single().Result);
        Assert.True((await service.DeleteRepairAsync(new(Guid.NewGuid(), r.Id, f.Revision), Ct)).IsSuccess);
        Assert.Empty((await service.ListRepairsAsync(f.Id, cancellationToken: Ct)).Value!.Items);
    }

    [Fact]
    public async Task ReceiptFailureRollsBackFaultAndAnomalyAndAllowsRetry()
    {
        var operations = new SqliteOperationsService(store);
        var anomaly = (await operations.CreateAnomalyAsync(new(Guid.NewGuid(), device, Time, "异常"), Ct)).Value!;
        var command = new ConvertAnomalyCommand(Guid.NewGuid(), anomaly.Id, anomaly.Revision, Time, "OTHER", "LOW", "检查");
        await ExecuteSql("CREATE TRIGGER FailFaultReceipt BEFORE INSERT ON OperationReceipt WHEN NEW.ResultKind='FaultConvert' BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;");

        var error = await Assert.ThrowsAsync<SqliteException>(() => service.ConvertAnomalyAsync(command, Ct));
        Assert.Contains("injected receipt failure", error.Message);
        Assert.Equal(0, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
        Assert.Equal(anomaly, (await operations.GetAnomalyAsync(anomaly.Id, Ct)).Value);
        var receiptCount = await store.ReadAsync(async (db, ct) =>
        {
            using var query = db.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM OperationReceipt WHERE OperationId=$id";
            query.Parameters.AddWithValue("$id", command.OperationId.ToString("N"));
            return Convert.ToInt64(await query.ExecuteScalarAsync(ct));
        }, Ct);
        Assert.Equal(0, receiptCount);

        await ExecuteSql("DROP TRIGGER FailFaultReceipt;");
        Assert.True((await service.ConvertAnomalyAsync(command, Ct)).IsSuccess);
        Assert.Equal(1, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
        Assert.Equal("CONVERTED", (await operations.GetAnomalyAsync(anomaly.Id, Ct)).Value!.Status);
    }

    [Fact]
    public async Task InvalidTimesAndStaleRevisionLeaveAggregateUnchanged()
    {
        var fault = await Create();
        Assert.Equal("StartedAt", (await service.StartFaultAsync(new(Guid.NewGuid(), fault.Id, fault.Revision, Time.AddSeconds(-1)), Ct)).Error!.Field);
        Assert.Equal("RepairedAt", (await service.AddRepairAsync(new(Guid.NewGuid(), fault.Id, fault.Revision, Time.AddSeconds(-1), "检查", "正常", "", ""), Ct)).Error!.Field);
        Assert.Equal(fault, (await service.GetFaultAsync(fault.Id, Ct)).Value);
        var started = (await service.StartFaultAsync(new(Guid.NewGuid(), fault.Id, fault.Revision, Time), Ct)).Value!;
        Assert.Equal(ErrorCodes.ConcurrentChange, (await service.AddRepairAsync(new(Guid.NewGuid(), fault.Id, fault.Revision, Time, "检查", "正常", "", ""), Ct)).Error!.Code);
        Assert.Equal(started, (await service.GetFaultAsync(fault.Id, Ct)).Value);
        Assert.Empty((await service.ListRepairsAsync(fault.Id, cancellationToken: Ct)).Value!.Items);
    }

    [Fact]
    public async Task ReusingOperationWithDifferentPayloadCannotCreateAnotherFault()
    {
        var command = new CreateFaultCommand(Guid.NewGuid(), device, Time, "OTHER", "LOW", "检查");
        var fault = (await service.CreateFaultAsync(command, Ct)).Value!;
        Assert.Equal(fault, (await service.CreateFaultAsync(command, Ct)).Value);
        Assert.Equal("OperationId", (await service.CreateFaultAsync(command with { Description = "不同内容" }, Ct)).Error!.Field);
        Assert.Equal("OperationId", (await service.CreateFaultAsync(command with { OperationId = Guid.Empty }, Ct)).Error!.Field);
        Assert.Equal(1, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task InactiveDeviceBlocksCreationAndConversion()
    {
        var operations = new SqliteOperationsService(store);
        var anomaly = (await operations.CreateAnomalyAsync(new(Guid.NewGuid(), device, Time, "异常"), Ct)).Value!;
        var registry = new SqliteRegistryService(store);
        var record = (await registry.GetDeviceAsync(device, Ct)).Value!;
        Assert.True((await registry.DeactivateDeviceAsync(new(Guid.NewGuid(), device, record.Revision), Ct)).IsSuccess);
        Assert.Equal("DeviceId", (await service.CreateFaultAsync(new(Guid.NewGuid(), device, Time, "OTHER", "LOW", "检查"), Ct)).Error!.Field);
        Assert.Equal("DeviceId", (await service.ConvertAnomalyAsync(new(Guid.NewGuid(), anomaly.Id, anomaly.Revision, Time, "OTHER", "LOW", "检查"), Ct)).Error!.Field);
        Assert.Equal(anomaly, (await operations.GetAnomalyAsync(anomaly.Id, Ct)).Value);
        Assert.Equal(0, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task SimulatedDeviceRejectsManualFaultAndMismatchedAnomaly()
    {
        var operations = new SqliteOperationsService(store);
        var anomaly = (await operations.CreateAnomalyAsync(new(Guid.NewGuid(), device, Time, "异常"), Ct)).Value!;
        await store.WriteAsync(async (db, tx, ct) =>
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "INSERT INTO SimulationDataset(Id) VALUES(1); UPDATE Device SET Source='SIMULATED',SimulationDatasetId=1 WHERE Id=$id";
            query.Parameters.AddWithValue("$id", device);
            return await query.ExecuteNonQueryAsync(ct);
        }, Ct);
        Assert.Equal(ErrorCodes.SourceMismatch, (await service.CreateFaultAsync(new(Guid.NewGuid(), device, Time, "OTHER", "LOW", "检查"), Ct)).Error!.Code);
        Assert.Equal(ErrorCodes.SourceMismatch, (await service.ConvertAnomalyAsync(new(Guid.NewGuid(), anomaly.Id, anomaly.Revision, Time, "OTHER", "LOW", "检查"), Ct)).Error!.Code);
        Assert.Equal(anomaly, (await operations.GetAnomalyAsync(anomaly.Id, Ct)).Value);
        Assert.Equal(0, (await service.ListFaultsAsync(new(), Ct)).Value!.TotalCount);
    }

    private Task<int> ExecuteSql(string sql) => store.WriteAsync(async (db, tx, ct) =>
    {
        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(ct);
    }, Ct);
}
