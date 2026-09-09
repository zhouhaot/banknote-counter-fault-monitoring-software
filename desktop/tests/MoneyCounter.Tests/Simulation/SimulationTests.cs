using Microsoft.Data.Sqlite;
using MoneyCounter.Core.Simulation;
using MoneyCounter.Infrastructure.Simulation;
using MoneyCounter.Infrastructure.Storage;
namespace MoneyCounter.Tests.Simulation;

public sealed class SimulationTests : IAsyncLifetime
{
    private readonly string path=Path.Combine(Path.GetTempPath(),$"simulation-{Guid.NewGuid():N}.db");
    private DbStore store=null!;
    private SqliteSimulationService service=null!;
    private static CancellationToken Ct=>TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync(){store=new(path);await store.InitializeAsync(Ct);service=new(store);}
    public async ValueTask DisposeAsync(){await store.DisposeAsync();File.Delete(path);}
    private async Task<long> Create(long seed=42){var r=await service.CreateAsync(new(Guid.NewGuid(),seed),Ct);Assert.True(r.IsSuccess,r.Error?.Message);return r.Value!.Id;}
    private Task<long> Count(string sql)=>store.ReadAsync(async(db,ct)=>{using var q=db.CreateCommand();q.CommandText=sql;return Convert.ToInt64(await q.ExecuteScalarAsync(ct));},Ct);
    private Task<int> Sql(string sql)=>store.WriteAsync(async(db,tx,ct)=>{using var q=db.CreateCommand();q.Transaction=tx;q.CommandText=sql;return await q.ExecuteNonQueryAsync(ct);},Ct);
    private Task<string> StatusSnapshot()=>store.ReadAsync(async(db,ct)=>{using var q=db.CreateCommand();q.CommandText="SELECT group_concat(v,';') FROM (SELECT d.AssetCode||':'||s.RecordedAt||':'||s.Status||':'||s.CumulativeCount v FROM StatusRecord s JOIN Device d ON d.Id=s.DeviceId ORDER BY d.AssetCode,s.RecordedAt)";return (string)(await q.ExecuteScalarAsync(ct))!;},Ct);
    [Fact] public async Task GeneratesCompleteDeterministicSampleAndResetIsIdempotent()
    {
        var create=new CreateSimulationCommand(Guid.NewGuid(),long.MinValue);
        var first=await service.CreateAsync(create,Ct);Assert.True(first.IsSuccess,first.Error?.Message);
        var again=await service.CreateAsync(create,Ct);Assert.Equal(first.Value,again.Value);
        string[] tables=["Model","Device","StatusRecord","Anomaly","Fault","Repair","Consumable","InventoryMovement"];
        long[] counts=[6,24,2160,8,8,6,4,8];
        for(int i=0;i<tables.Length;i++){Assert.Equal(counts[i],await Count($"SELECT COUNT(*) FROM {tables[i]}"));Assert.Equal(0,await Count($"SELECT COUNT(*) FROM {tables[i]} WHERE Source<>'SIMULATED' OR SimulationDatasetId<>{first.Value!.Id}"));}
        var snapshot=await StatusSnapshot();
        var reset=new ResetSimulationCommand(Guid.NewGuid(),first.Value!.Id);
        Assert.True((await service.ResetAsync(reset,Ct)).IsSuccess);
        Assert.True((await service.ResetAsync(reset,Ct)).IsSuccess);
        for(int i=0;i<tables.Length;i++)Assert.Equal(0,await Count($"SELECT COUNT(*) FROM {tables[i]}"));
        var second=await Create(long.MinValue);Assert.NotEqual(first.Value.Id,second);Assert.Equal(snapshot,await StatusSnapshot());
    }
    [Fact] public async Task DuplicateSeedAndOperationPayloadAreRejectedWithoutExtraRows()
    {
        var op=Guid.NewGuid();Assert.True((await service.CreateAsync(new(op,1),Ct)).IsSuccess);
        Assert.False((await service.CreateAsync(new(Guid.NewGuid(),1),Ct)).IsSuccess);
        Assert.False((await service.CreateAsync(new(op,2),Ct)).IsSuccess);
        Assert.Equal(1,await Count("SELECT COUNT(*) FROM SimulationDataset"));
    }
    [Fact] public async Task ManualAndCsvRowsRemainByteEquivalentAndOtherDatasetSurvives()
    {
        await Sql("INSERT INTO ImportBatch(Id) VALUES(900); INSERT INTO Model(Manufacturer,ModelName,IsActive,Revision,Source,CreatedAtUtc,UpdatedAtUtc) VALUES('人工','人工',1,0,'MANUAL','x','x'); INSERT INTO Model(Manufacturer,ModelName,IsActive,Revision,Source,ImportBatchId,CreatedAtUtc,UpdatedAtUtc) VALUES('导入','导入',1,0,'CSV',900,'x','x');");
        await SeedRealGraph();
        var before=await NonSimSnapshot();var one=await Create();var two=await Create(long.MaxValue);
        Assert.True((await service.ResetAsync(new(Guid.NewGuid(),one),Ct)).IsSuccess);
        Assert.Equal(before,await NonSimSnapshot());Assert.Equal(24,await Count($"SELECT COUNT(*) FROM Device WHERE SimulationDatasetId={two}"));
    }
    private async Task SeedRealGraph()
    {
        foreach (var source in new[] { "MANUAL", "CSV" })
        {
            var id = source == "MANUAL" ? 900 : 901; var batch = source == "CSV" ? "900" : "NULL";
            await Sql($"""
                INSERT INTO Device(Id,AssetCode,ModelId,IsActive,Revision,Source,ImportBatchId,CreatedAtUtc,UpdatedAtUtc)
                  VALUES({id},'REAL-{source}',(SELECT Id FROM Model WHERE Source='{source}'),1,0,'{source}',{batch},'2026','2026');
                INSERT INTO StatusRecord(DeviceId,RecordedAt,Status,CumulativeCount,Notes,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},'2026-01-01T00:00:00.0000000Z','RUNNING',123,'真实状态','{source}',{batch},'2026');
                INSERT INTO Anomaly(Id,DeviceId,DiscoveredAt,Description,Status,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},{id},'2026-01-01T00:00:00.0000000Z','真实异常','CONVERTED','{source}',{batch},'2026');
                INSERT INTO Fault(Id,FaultNo,DeviceId,SourceAnomalyId,RegisteredAt,FaultType,Severity,Description,Status,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},'REAL-{source}',{id},{id},'2026-01-01T00:00:00.0000000Z','OTHER','LOW','真实故障','PENDING','{source}',{batch},'2026');
                INSERT INTO Repair(FaultId,RepairedAt,Action,Result,Technician,Notes,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},'2026-01-01T01:00:00.0000000Z','检查','正常','张三','真实维修','{source}',{batch},'2026');
                INSERT INTO Consumable(Id,Name,Unit,Notes,Source,ImportBatchId) VALUES({id},'REAL-{source}','件','','{source}',{batch});
                INSERT INTO InventoryMovement(ConsumableId,MovementType,QuantityMinor,OccurredAt,Reason,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},'INBOUND',1000,'2026-01-01T00:00:00.0000000Z','真实入库','{source}',{batch},'2026');
                INSERT INTO InventoryMovement(ConsumableId,MovementType,QuantityMinor,OccurredAt,Reason,DeviceId,FaultId,Source,ImportBatchId,CreatedAtUtc)
                  VALUES({id},'ISSUE',-125,'2026-01-01T01:00:00.0000000Z','真实领用',{id},{id},'{source}',{batch},'2026');
                """);
        }
    }

    private Task<string> NonSimSnapshot() => store.ReadAsync(async (db, ct) =>
    {
        string[] tables = ["Model", "Device", "StatusRecord", "Anomaly", "Fault", "Repair", "Consumable", "InventoryMovement", "ImportBatch"];
        var records = new List<object>();
        foreach (var table in tables)
        {
            using var query = db.CreateCommand();
            query.CommandText = $"SELECT * FROM {table} " + (table == "ImportBatch" ? "" : "WHERE Source<>'SIMULATED' ") + "ORDER BY Id";
            using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values); records.Add(new { Table = table, Values = values });
            }
        }
        return System.Text.Json.JsonSerializer.Serialize(records);
    }, Ct);    [Fact] public async Task OrdinaryConnectionCannotDeleteSimulationLedger()
    {
        await Create();await Assert.ThrowsAsync<SqliteException>(()=>Sql("DELETE FROM InventoryMovement"));Assert.Equal(8,await Count("SELECT COUNT(*) FROM InventoryMovement"));
    }
    [Fact] public async Task CrossDatasetDeviceModelIsRejectedByTrigger()
    {
        var one=await Create(1);var two=await Create(2);
        await Assert.ThrowsAsync<SqliteException>(()=>Sql($"UPDATE Device SET ModelId=(SELECT MIN(Id) FROM Model WHERE SimulationDatasetId={two}) WHERE SimulationDatasetId={one}"));
    }
    [Fact] public async Task CorruptCrossDatasetGraphPreventsAllDeletion()
    {
        var one=await Create(1);var two=await Create(2);
        await Sql($"UPDATE StatusRecord SET RecordedAt='2026-09-01T00:00:00.0000000Z',DeviceId=(SELECT MAX(Id) FROM Device WHERE SimulationDatasetId={two}) WHERE Id=(SELECT MIN(Id) FROM StatusRecord WHERE SimulationDatasetId={one});");
        var r=await service.ResetAsync(new(Guid.NewGuid(),one),Ct);Assert.False(r.IsSuccess);Assert.Equal("SourceMismatch",r.Error!.Code);Assert.Equal(48,await Count("SELECT COUNT(*) FROM Device"));Assert.Equal(16,await Count("SELECT COUNT(*) FROM InventoryMovement"));
    }
    [Fact] public async Task ExternalReversalReferencePreventsReset()
    {
        var one=await Create(1);var two=await Create(2);
        await Sql($"DROP TRIGGER InventoryMovement_NoUpdate; UPDATE InventoryMovement SET MovementType='REVERSAL',ReversesId=(SELECT MIN(Id) FROM InventoryMovement WHERE SimulationDatasetId={one}) WHERE Id=(SELECT MIN(Id) FROM InventoryMovement WHERE SimulationDatasetId={two});");
        Assert.False((await service.ResetAsync(new(Guid.NewGuid(),one),Ct)).IsSuccess);Assert.Equal(16,await Count("SELECT COUNT(*) FROM InventoryMovement"));
    }
    [Fact] public async Task MixedSourceMetadataPreventsReset()
    {
        var one=await Create();
        await store.WriteAsync(async(db,tx,ct)=>{using var q=db.CreateCommand();q.Transaction=tx;q.CommandText=$"PRAGMA ignore_check_constraints=ON;UPDATE Anomaly SET Source='MANUAL' WHERE SimulationDatasetId={one};";return await q.ExecuteNonQueryAsync(ct);},Ct);
        Assert.False((await service.ResetAsync(new(Guid.NewGuid(),one),Ct)).IsSuccess);Assert.Equal(24,await Count("SELECT COUNT(*) FROM Device"));
    }
    [Fact] public async Task CollisionRollsBackWholeGeneratedDataset()
    {
        await Sql("INSERT INTO Consumable(Name,Unit,Notes,Source) VALUES('模拟耗材-42-0','件','','MANUAL')");
        Assert.False((await service.CreateAsync(new(Guid.NewGuid(),42),Ct)).IsSuccess);Assert.Equal(0,await Count("SELECT COUNT(*) FROM SimulationDataset"));Assert.Equal(0,await Count("SELECT COUNT(*) FROM Model"));Assert.Equal(0,await Count("SELECT COUNT(*) FROM OperationReceipt"));
    }
    [Fact]
    public async Task RegistryCreateAndUpdatePreserveSimulationBoundaries()
    {
        var one = await Create(1); var two = await Create(2); var registry = new SqliteRegistryService(store);
        long modelOne = await Count($"SELECT MIN(Id) FROM Model WHERE SimulationDatasetId={one}");
        long modelTwo = await Count($"SELECT MIN(Id) FROM Model WHERE SimulationDatasetId={two}");
        long deviceOne = await Count($"SELECT MIN(Id) FROM Device WHERE SimulationDatasetId={one}");
        var realModel = await registry.CreateModelAsync(new(Guid.NewGuid(), "人工", "型号", null, ""), Ct);
        Assert.False((await registry.CreateDeviceAsync(new(Guid.NewGuid(), "真实设备", modelOne, null, null, "", "", ""), Ct)).IsSuccess);
        var realDevice = await registry.CreateDeviceAsync(new(Guid.NewGuid(), "真实设备", realModel.Value!.Id, null, null, "", "", ""), Ct);
        Assert.True(realDevice.IsSuccess);
        Assert.False((await registry.UpdateDeviceAsync(new(Guid.NewGuid(), realDevice.Value!.Id, 0, "真实设备", modelOne, null, null, "", "", ""), Ct)).IsSuccess);
        Assert.False((await registry.UpdateDeviceAsync(new(Guid.NewGuid(), deviceOne, 0, "SIM-1-000", modelTwo, null, null, "", "", ""), Ct)).IsSuccess);
        Assert.False((await registry.UpdateDeviceAsync(new(Guid.NewGuid(), deviceOne, 0, "SIM-1-000", realModel.Value.Id, null, null, "", "", ""), Ct)).IsSuccess);
        Assert.True((await registry.UpdateDeviceAsync(new(Guid.NewGuid(), deviceOne, 0, "SIM-1-000", modelOne, null, null, "调整位置", "", ""), Ct)).IsSuccess);
        Assert.Equal(49, await Count("SELECT COUNT(*) FROM Device"));
    }}





