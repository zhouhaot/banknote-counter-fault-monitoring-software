using Microsoft.Data.Sqlite;
using MoneyCounter.Core.Inventory;
using MoneyCounter.Infrastructure.Inventory;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Inventory;

public sealed class InventoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"inventory-{Guid.NewGuid():N}.db");
    private DbStore _store = null!;
    private SqliteInventoryService _service = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        _store = new(_path);
        await _store.InitializeAsync(Ct);
        _service = new(_store);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        File.Delete(_path);
    }

    private async Task<ConsumableDetail> Item()
    {
        var result = await _service.CreateConsumableAsync(new(Guid.NewGuid(), "清洁剂", "瓶", ""), Ct);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private Task<MoneyCounter.Core.Result<MovementDetail>> Post(long id, decimal quantity, string kind = "INBOUND") =>
        _service.PostMovementAsync(new(Guid.NewGuid(), id, kind, quantity, At, "盘点"), Ct);

    [Fact]
    public async Task DecimalStockIsExactAndNegativeStockIsRejected()
    {
        var item = await Item();
        Assert.True((await Post(item.Id, .1m)).IsSuccess);
        Assert.True((await Post(item.Id, .2m)).IsSuccess);
        Assert.False((await Post(item.Id, -.31m, "ADJUST")).IsSuccess);
        Assert.True((await Post(item.Id, -.3m, "ADJUST")).IsSuccess);
        Assert.Equal(0, (await _service.ListConsumablesAsync(new(), Ct)).Value!.Items.Single().StockMinor);
        Assert.Equal(3, (await _service.ListMovementsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.001")]
    [InlineData("10000000000")]
    [InlineData("-1")]
    public async Task InvalidQuantitiesDoNotWrite(string value)
    {
        var item = await Item();
        Assert.False((await Post(item.Id, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))).IsSuccess);
        Assert.Equal(0, (await _service.ListMovementsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task MaximumAndFractionalQuantityPersistExactly()
    {
        var item = await Item();
        var posted = await Post(item.Id, InventoryValues.MaximumQuantity);
        Assert.True(posted.IsSuccess);
        Assert.Equal(999999999999L, posted.Value!.QuantityMinor);
        Assert.Equal(InventoryValues.MaximumQuantity, (await _service.ListConsumablesAsync(new(), Ct)).Value!.Items.Single().CurrentStock);
    }

    [Fact]
    public async Task ParallelRetriesOnlyCreateOneMovementAndChangedPayloadIsRejected()
    {
        var item = await Item();
        var command = new PostMovementCommand(Guid.NewGuid(), item.Id, "INBOUND", 1.23m, At, "入库");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.PostMovementAsync(command, Ct)));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result => result.Value!.Id).Distinct());
        Assert.False((await _service.PostMovementAsync(command with { Quantity = 2m }, Ct)).IsSuccess);
        Assert.Equal(123, (await _service.ListConsumablesAsync(new(), Ct)).Value!.Items.Single().StockMinor);
    }

    [Fact]
    public async Task ConcurrentDeductionsCannotOverspend()
    {
        var item = await Item();
        await Post(item.Id, 1);
        var results = await Task.WhenAll(Post(item.Id, -.75m, "ADJUST"), Post(item.Id, -.75m, "ADJUST"));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(25, (await _service.ListConsumablesAsync(new(), Ct)).Value!.Items.Single().StockMinor);
    }

    [Fact]
    public async Task ReversalPreservesHistoryAndCanItselfBeCorrected()
    {
        var item = await Item();
        var original = (await Post(item.Id, 2)).Value!;
        var reversal = await _service.ReverseMovementAsync(new(Guid.NewGuid(), original.Id, At, "撤销"), Ct);
        Assert.True(reversal.IsSuccess);
        Assert.Equal(-200, reversal.Value!.QuantityMinor);
        Assert.False((await _service.ReverseMovementAsync(new(Guid.NewGuid(), original.Id, At, "重复"), Ct)).IsSuccess);
        Assert.True((await _service.ReverseMovementAsync(new(Guid.NewGuid(), reversal.Value.Id, At, "恢复"), Ct)).IsSuccess);
        Assert.Equal(200, (await _service.ListConsumablesAsync(new(), Ct)).Value!.Items.Single().StockMinor);
        Assert.Equal(3, (await _service.ListMovementsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task ReversingConsumedInboundAndBackdatedReversalAreRejected()
    {
        var item = await Item();
        var original = (await Post(item.Id, 2)).Value!;
        await Post(item.Id, -1, "ADJUST");
        Assert.False((await _service.ReverseMovementAsync(new(Guid.NewGuid(), original.Id, At, "库存不足"), Ct)).IsSuccess);
        Assert.False((await _service.ReverseMovementAsync(new(Guid.NewGuid(), original.Id, At.AddSeconds(-1), "时间错误"), Ct)).IsSuccess);
    }

    [Fact]
    public async Task UnitBecomesImmutableAfterFirstMovementAndDeactivatedItemAllowsCorrectionOnly()
    {
        var item = await Item();
        var first = (await Post(item.Id, 1)).Value!;
        Assert.False((await _service.UpdateConsumableAsync(new(Guid.NewGuid(), item.Id, item.Revision, item.Name, "箱", ""), Ct)).IsSuccess);
        Assert.True((await _service.DeactivateConsumableAsync(new(Guid.NewGuid(), item.Id, item.Revision), Ct)).IsSuccess);
        Assert.False((await Post(item.Id, 1)).IsSuccess);
        Assert.True((await _service.ReverseMovementAsync(new(Guid.NewGuid(), first.Id, At, "停用后更正"), Ct)).IsSuccess);
    }

    [Theory]
    [InlineData("UPDATE InventoryMovement SET Reason='changed'")]
    [InlineData("DELETE FROM InventoryMovement")]
    [InlineData("UPDATE Consumable SET Unit='箱'")]
    [InlineData("DELETE FROM Consumable")]
    public async Task DatabaseProtectsHistoricalRecords(string sql)
    {
        await Post((await Item()).Id, 1);
        var error = await Assert.ThrowsAsync<SqliteException>(() => Sql(sql));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Theory]
    [InlineData("'ADJUST',-200,NULL,'MANUAL',NULL")]
    [InlineData("'REVERSAL',-50,1,'MANUAL',NULL")]
    [InlineData("'INBOUND',100,NULL,'SIMULATED',1")]
    public async Task DatabaseRejectsInsufficientStockInvalidReversalAndMixedSource(string values)
    {
        await Post((await Item()).Id, 1);
        var error = await Assert.ThrowsAsync<SqliteException>(() => Sql("INSERT INTO InventoryMovement(ConsumableId,OccurredAt,Reason,CreatedAtUtc,MovementType,QuantityMinor,ReversesId,Source,SimulationDatasetId) VALUES(1,'2026-01-01T00:00:00.0000000Z','test','2026-01-01T00:00:00.0000000Z'," + values + ")"));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public async Task IssueRequiresDeviceAndRejectsFaultFromAnotherDevice()
    {
        var item = await Item();
        await Post(item.Id, 5);
        var registry = new SqliteRegistryService(_store);
        var model = (await registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "测试型号", null, ""), Ct)).Value!;
        var device = (await registry.CreateDeviceAsync(new(Guid.NewGuid(), "A001", model.Id, null, null, "", "", ""), Ct)).Value!;
        var other = (await registry.CreateDeviceAsync(new(Guid.NewGuid(), "A002", model.Id, null, null, "", "", ""), Ct)).Value!;
        var maintenance = new MoneyCounter.Infrastructure.Maintenance.SqliteMaintenanceService(_store);
        var fault = (await maintenance.CreateFaultAsync(new(Guid.NewGuid(), other.Id, At, "OTHER", "LOW", "测试故障"), Ct)).Value!;
        Assert.False((await Post(item.Id, 1, "ISSUE")).IsSuccess);
        var command = new PostMovementCommand(Guid.NewGuid(), item.Id, "ISSUE", 1, At, "更换", device.Id, fault.Id);
        Assert.False((await _service.PostMovementAsync(command, Ct)).IsSuccess);
        var valid = await _service.PostMovementAsync(command with { OperationId = Guid.NewGuid(), DeviceId = other.Id }, Ct);
        Assert.True(valid.IsSuccess, valid.Error?.Message);
        Assert.Equal(-100, valid.Value!.QuantityMinor);
        Assert.Single((await _service.ListMovementsAsync(new(FaultId: fault.Id, DeviceId: other.Id), Ct)).Value!.Items);
    }

    [Fact]
    public async Task SimulatedConsumableRejectsManualAndCrossDatasetMovements()
    {
        await Sql("INSERT INTO SimulationDataset(Id) VALUES(1),(2); INSERT INTO Consumable(Name,Unit,Notes,Source,SimulationDatasetId) VALUES('模拟耗材','个','','SIMULATED',1)");
        Assert.False((await Post(1, 1)).IsSuccess);
        var error = await Assert.ThrowsAsync<SqliteException>(() => Sql("INSERT INTO InventoryMovement(ConsumableId,MovementType,QuantityMinor,OccurredAt,Reason,Source,SimulationDatasetId,CreatedAtUtc) VALUES(1,'INBOUND',100,'2026-01-01','test','SIMULATED',2,'2026-01-01')"));
        Assert.Contains("Invalid consumable source", error.Message);
        await Sql("INSERT INTO InventoryMovement(ConsumableId,MovementType,QuantityMinor,OccurredAt,Reason,Source,SimulationDatasetId,CreatedAtUtc) VALUES(1,'INBOUND',100,'2026-01-01','test','SIMULATED',1,'2026-01-01')");
        Assert.False((await _service.ReverseMovementAsync(new(Guid.NewGuid(), 1, At.AddDays(1), "人工撤销模拟"), Ct)).IsSuccess);
    }

    [Theory]
    [InlineData("", "瓶", "Name")]
    [InlineData("清洁剂", "", "Unit")]
    public async Task ValidationTargetsTheInvalidField(string name, string unit, string field)
    {
        var result = await _service.CreateConsumableAsync(new(Guid.NewGuid(), name, unit, ""), Ct);
        Assert.False(result.IsSuccess);
        Assert.Equal(field, result.Error!.Field);
    }

    private Task<int> Sql(string sql) => _store.WriteAsync(async (db, tx, token) =>
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(token);
    }, Ct);
}
