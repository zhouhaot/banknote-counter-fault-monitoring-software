using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Simulation;
using MoneyCounter.Infrastructure.Storage;
namespace MoneyCounter.Infrastructure.Simulation;

public sealed class SqliteSimulationService(DbStore store) : ISimulationService
{
    private static readonly string[] Tables = ["InventoryMovement", "Repair", "Fault", "Anomaly", "StatusRecord", "Device", "Consumable", "Model"];
    private static readonly (string Child, string Column, string Parent)[] Edges =
    [ ("Device","ModelId","Model"), ("StatusRecord","DeviceId","Device"), ("Anomaly","DeviceId","Device"),
      ("Fault","DeviceId","Device"), ("Fault","SourceAnomalyId","Anomaly"), ("Repair","FaultId","Fault"),
      ("InventoryMovement","ConsumableId","Consumable"), ("InventoryMovement","DeviceId","Device"),
      ("InventoryMovement","FaultId","Fault"), ("InventoryMovement","ReversesId","InventoryMovement") ];
    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static Result<T> Fail<T>(string message, string code = ErrorCodes.InvalidRecord) => Result<T>.Failure(code,message);
    public Task<Result<IReadOnlyList<SimulationDetail>>> ListAsync(CancellationToken cancellationToken = default) => store.ReadAsync(async (db,ct) =>
    {
        using var q = Cmd(db,null,"SELECT Id,COALESCE(Seed,0),Name,CreatedAtUtc,GeneratorVersion FROM SimulationDataset ORDER BY Id DESC");
        using var r = await q.ExecuteReaderAsync(ct); var rows = new List<SimulationDetail>();
        while(await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture),r.GetString(4)));
        return Result<IReadOnlyList<SimulationDetail>>.Success(rows);
    },cancellationToken);
    public Task<Result<SimulationDetail>> CreateAsync(CreateSimulationCommand c, CancellationToken cancellationToken = default) => Execute(c.OperationId,c,"SimulationCreate",async (db,tx,ct) =>
    {
        if(await Scalar(db,tx,"SELECT COUNT(*) FROM SimulationDataset WHERE Seed=$id",c.Seed,ct)>0) return Fail<SimulationDetail>("该种子的模拟集已存在。",ErrorCodes.DuplicateRecord);
        var now=DateTimeOffset.UtcNow; var name=$"模拟集 {c.Seed}";
        var previousId=await Scalar(db,tx,"SELECT MAX(v) FROM (SELECT COALESCE(MAX(Id),0) v FROM SimulationDataset UNION ALL SELECT COALESCE(MAX(ResultId),0) FROM OperationReceipt WHERE ResultKind='SimulationCreate')",0,ct);
        if(previousId==long.MaxValue) return Fail<SimulationDetail>("模拟集标识空间已用尽。");
        long dataset=await Insert(db,tx,"SimulationDataset",ct,("Id",previousId+1),("Seed",c.Seed),("Name",name),("CreatedAtUtc",Stamp(now)),("GeneratorVersion","native-v1"));
        await Populate(db,tx,dataset,c.Seed,ct);
        return Result<SimulationDetail>.Success(new(dataset,c.Seed,name,now,"native-v1"));
    }, x=>x.Id,cancellationToken);
    public Task<Result<SimulationResetResult>> ResetAsync(ResetSimulationCommand c,CancellationToken cancellationToken=default) => Execute(c.OperationId,c,"SimulationReset",async(db,tx,ct)=>
    {
        if(await Scalar(db,tx,"SELECT COUNT(*) FROM SimulationDataset WHERE Id=$id",c.DatasetId,ct)==0) return Fail<SimulationResetResult>("模拟集不存在。",ErrorCodes.RecordNotFound);
        var counts=new Dictionary<string,long>();
        foreach(var table in Tables)
        {
            if(await Scalar(db,tx,$"SELECT COUNT(*) FROM {table} WHERE SimulationDatasetId=$id AND (Source<>'SIMULATED' OR ImportBatchId IS NOT NULL)",c.DatasetId,ct)>0) return Fail<SimulationResetResult>("模拟集含有来源不一致的数据，重置已取消。",ErrorCodes.SourceMismatch);
            counts[table]=await Scalar(db,tx,$"SELECT COUNT(*) FROM {table} WHERE SimulationDatasetId=$id",c.DatasetId,ct);
        }
        foreach(var (child,column,parent) in Edges)
        {
            var sql=$"SELECT COUNT(*) FROM {child} c LEFT JOIN {parent} p ON p.Id=c.{column} WHERE c.{column} IS NOT NULL AND ((c.SimulationDatasetId=$id AND (p.Id IS NULL OR p.Source<>'SIMULATED' OR p.SimulationDatasetId IS NOT $id)) OR (p.SimulationDatasetId=$id AND (c.Source<>'SIMULATED' OR c.SimulationDatasetId IS NOT $id)))";
            if(await Scalar(db,tx,sql,c.DatasetId,ct)>0) return Fail<SimulationResetResult>("存在跨来源或跨模拟集引用，重置已取消。",ErrorCodes.SourceMismatch);
        }
        db.CreateFunction<long?>("simulation_reset_dataset",()=>c.DatasetId);
        try
        {
            foreach(var table in Tables) { using var q=Cmd(db,tx,$"DELETE FROM {table} WHERE SimulationDatasetId=$id",("$id",c.DatasetId)); await q.ExecuteNonQueryAsync(ct); }
            using var last=Cmd(db,tx,"DELETE FROM SimulationDataset WHERE Id=$id",("$id",c.DatasetId)); await last.ExecuteNonQueryAsync(ct);
        }
        finally { db.CreateFunction<long?>("simulation_reset_dataset",()=>null); }
        return Result<SimulationResetResult>.Success(new(c.DatasetId,counts));
    },x=>x.DatasetId,cancellationToken);
    private static async Task Populate(SqliteConnection db,SqliteTransaction tx,long dataset,long seed,CancellationToken ct)
    {
        var anchor=new DateTimeOffset(2026,8,29,0,0,0,TimeSpan.Zero); var created=Stamp(anchor);
        // A specified integer generator keeps the sample reproducible across .NET versions.
        ulong state=unchecked((ulong)seed); uint Next() { state=unchecked(state*6364136223846793005UL+1442695040888963407UL); return (uint)(state>>32); }
        async Task<long> Row(string table,params (string,object?)[] fields) => await Insert(db,tx,table,ct,fields.Concat(new[]{("Source",(object?)"SIMULATED"),("SimulationDatasetId",(object?)dataset)}).ToArray());
        var models=new long[6]; var devices=new long[24];
        for(int i=0;i<6;i++) models[i]=await Row("Model",("Manufacturer",$"模拟厂商-{seed}-{i}"),("ModelName",$"模拟型号-{i:00}"),("RatedCountLife",1000000+i*100000),("Notes","确定性模拟数据"),("IsActive",1),("Revision",0),("CreatedAtUtc",created),("UpdatedAtUtc",created));
        for(int i=0;i<24;i++)
        {
            devices[i]=await Row("Device",("AssetCode",$"SIM-{seed}-{i:000}"),("ModelId",models[i%6]),("CommissionedOn","2025-01-01"),("PurchasedOn","2024-12-01"),("Location",$"模拟场地-{i%6+1}"),("ResponsiblePerson",$"模拟责任人-{i%8+1}"),("Notes","不连接真实点钞机"),("IsActive",1),("Revision",0),("CreatedAtUtc",created),("UpdatedAtUtc",created));
            long count=100+Next()%200;
            for(int day=0;day<90;day++) { count+=51+Next()%150; await Row("StatusRecord",("DeviceId",devices[i]),("RecordedAt",Stamp(anchor.AddDays(day-89))),("Status",day%11==0?"FAULT":day%13==0?"MAINTENANCE":(day+i)%7==0?"STOPPED":"RUNNING"),("CumulativeCount",count),("Notes","模拟日读数"),("CreatedAtUtc",created)); }
        }
        string[] types=["CARD_JAM","COUNTING","AUTHENTICATION","FEED","DISPLAY","POWER","MECHANICAL","OTHER"];
        string[] severities=["LOW","MEDIUM","HIGH","URGENT"];
        for(int i=0;i<8;i++)
        {
            var discovered=anchor.AddDays(i-40); var registered=anchor.AddDays(i-35);
            await Row("Anomaly",("DeviceId",devices[i]),("DiscoveredAt",Stamp(discovered)),("Description",$"模拟异常-{i+1}"),("Status",i>=4?"CLOSED":"OPEN"),("HandlingNotes",i>=4?"模拟处理完成":""),("ClosedAt",i>=4?Stamp(discovered.AddHours(4)):null),("CreatedAtUtc",created));
            var fault=await Row("Fault",("FaultNo",$"SIM-GZ-{seed}-{i:000}"),("DeviceId",devices[i]),("RegisteredAt",Stamp(registered)),("FaultType",types[i]),("Severity",severities[i%4]),("Description",$"模拟故障-{types[i]}"),("Status",i==0?"PENDING":i==1?"IN_PROGRESS":"CLOSED"),("StartedAt",i>0?Stamp(registered.AddHours(2)):null),("ClosedAt",i>1?Stamp(registered.AddHours(8)):null),("FinalResult",i>1?"模拟维修完成":""),("CreatedAtUtc",created));
            if(i>1) await Row("Repair",("FaultId",fault),("RepairedAt",Stamp(registered.AddHours(6))),("Action","清洁与检查"),("Result","模拟测试正常"),("Technician","模拟人员"),("Notes","模拟维修记录"),("CreatedAtUtc",created));
        }
        for(int i=0;i<4;i++)
        {
            var item=await Row("Consumable",("Name",$"模拟耗材-{seed}-{i}"),("Unit","件"),("Notes","模拟库存"));
            await Row("InventoryMovement",("ConsumableId",item),("MovementType","INBOUND"),("QuantityMinor",10000),("OccurredAt",Stamp(anchor.AddDays(-30))),("Reason","模拟入库"),("CreatedAtUtc",created));
            await Row("InventoryMovement",("ConsumableId",item),("MovementType","ISSUE"),("QuantityMinor",-500-i*100),("DeviceId",devices[i]),("OccurredAt",Stamp(anchor.AddDays(-10))),("Reason","模拟领用"),("CreatedAtUtc",created));
        }
    }
    private async Task<Result<T>> Execute<T>(Guid op,object payload,string kind,Func<SqliteConnection,SqliteTransaction,CancellationToken,Task<Result<T>>> action,Func<T,long> id,CancellationToken ct)
    {
        if(op==Guid.Empty) return Fail<T>("操作标识不能为空。");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind+JsonSerializer.Serialize(payload))));
        try { return await store.WriteAsync(async(db,tx,token)=>
        {
            using(var q=Cmd(db,tx,"SELECT PayloadHash,ResultKind,ResultJson FROM OperationReceipt WHERE OperationId=$op",("$op",op.ToString("N"))))
            using(var r=await q.ExecuteReaderAsync(token)) if(await r.ReadAsync(token))
            { if(r.GetString(0)!=hash||r.GetString(1)!=kind) return Fail<T>("操作标识不能复用于不同内容。"); return Result<T>.Success(JsonSerializer.Deserialize<T>(r.GetString(2))??throw new InvalidDataException("Invalid receipt")); }
            var result=await action(db,tx,token); if(!result.IsSuccess) return result;
            using var receipt=Cmd(db,tx,"INSERT INTO OperationReceipt VALUES($op,$hash,$kind,$id,$json,$now)",("$op",op.ToString("N")),("$hash",hash),("$kind",kind),("$id",id(result.Value!)),("$json",JsonSerializer.Serialize(result.Value)),("$now",Stamp(DateTimeOffset.UtcNow))); await receipt.ExecuteNonQueryAsync(token); return result;
        },ct); }
        catch(SqliteException ex) when(ex.SqliteErrorCode==19) { return Fail<T>("数据标识冲突或关联约束不满足，操作已全部回滚。",ErrorCodes.InvalidRecord); }
    }
    private static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction? tx,string sql,params(string,object?)[] fields) { var q=db.CreateCommand();q.Transaction=tx;q.CommandText=sql;foreach(var(k,v)in fields)q.Parameters.AddWithValue(k,v??DBNull.Value);return q; }
    private static async Task<long> Scalar(SqliteConnection db,SqliteTransaction tx,string sql,long id,CancellationToken ct) {using var q=Cmd(db,tx,sql,("$id",id));return Convert.ToInt64(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);}
    private static async Task<long> Insert(SqliteConnection db,SqliteTransaction tx,string table,CancellationToken ct,params(string,object?)[] fields)
    {
        using var q=Cmd(db,tx,$"INSERT INTO {table}({string.Join(',',fields.Select(x=>x.Item1))}) VALUES({string.Join(',',fields.Select((_,i)=>"$p"+i))}); SELECT last_insert_rowid();",fields.Select((x,i)=>("$p"+i,x.Item2)).ToArray()); return Convert.ToInt64(await q.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
    }
}
