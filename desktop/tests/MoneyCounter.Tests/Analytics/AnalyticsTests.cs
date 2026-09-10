using System.Globalization;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Registry;
using MoneyCounter.Infrastructure.Analytics;
using MoneyCounter.Infrastructure.Operations;
using MoneyCounter.Infrastructure.Storage;
using MoneyCounter.Infrastructure.Simulation;
using MoneyCounter.Infrastructure.Inventory;

namespace MoneyCounter.Tests.Analytics;
public sealed class AnalyticsTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"analytics-{Guid.NewGuid():N}.db");
    private DbStore store = null!;
    private SqliteAnalyticsService analytics = null!;
    private SqliteRegistryService registry = null!;
    private long model, device;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateOnly Day = new(2026, 8, 31);
    private static DateTimeOffset At(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(8));
    public async ValueTask InitializeAsync()
    {
        store = new(path); await store.InitializeAsync(Ct); analytics = new(store); registry = new(store);
        model = (await registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "统计型号", 1000, ""), Ct)).Value!.Id;
        device = await Device("A", model, new(2026,8,1));
    }
    public async ValueTask DisposeAsync() { await store.DisposeAsync(); File.Delete(path); }
    private async Task<long> Device(string code, long id, DateOnly? day = null) => (await registry.CreateDeviceAsync(new(Guid.NewGuid(), code, id, day, null, "", "", ""), Ct)).Value!.Id;
    private async Task Status(long id, DateTimeOffset at, long count, string status = "RUNNING") => Assert.True((await new SqliteOperationsService(store).RecordStatusAsync(new(Guid.NewGuid(), id, at, status, count, ""), Ct)).IsSuccess);
    private Task<int> Sql(string sql, params (string, object?)[] values) => store.WriteAsync(async (db, tx, ct) => { using var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach(var (name,value) in values) q.Parameters.AddWithValue(name,value ?? DBNull.Value); return await q.ExecuteNonQueryAsync(ct); }, Ct);
    private Task Fault(DateTimeOffset registered, decimal? seconds = null, string type = "CARD_JAM", string severity = "HIGH") => Sql("INSERT INTO Fault(FaultNo,DeviceId,RegisteredAt,FaultType,Severity,Description,Status,StartedAt,ClosedAt,FinalResult,Source,CreatedAtUtc) VALUES($no,$device,$at,$type,$severity,'测试',$status,$started,$closed,$result,'MANUAL',$at)", ("$no", Guid.NewGuid().ToString()), ("$device",device), ("$at", registered.UtcDateTime.ToString("O")), ("$type",type), ("$severity",severity), ("$status",seconds.HasValue?"CLOSED":"PENDING"), ("$started",seconds.HasValue?registered.UtcDateTime.ToString("O"):null), ("$closed",seconds.HasValue?registered.AddTicks((long)(seconds.Value*TimeSpan.TicksPerSecond)).UtcDateTime.ToString("O"):null), ("$result",seconds.HasValue?"已处理":""));
    [Fact] public async Task LifetimeUsesLastReadingBeforeNextBeijingDayAndStatusFilterAfterSelection()
    {
        await Status(device, At(8,31,23), 1250, "STOPPED"); await Status(device, At(9,1), 1500);
        var row = Assert.Single((await analytics.DeviceLifeAsync(new(Day,LatestStatus:"STOPPED"),Ct)).Value!.Items);
        Assert.Equal(125m,row.UtilizationPercent); Assert.Equal(30,row.AgeDays); Assert.Equal(1250,row.LatestCount);
        Assert.Empty((await analytics.DeviceLifeAsync(new(Day,LatestStatus:"RUNNING"),Ct)).Value!.Items);
        Assert.Empty((await analytics.DeviceLifeAsync(new(Day,ModelId:model+99,DeviceId:device),Ct)).Value!.Items);
    }
    [Fact] public async Task MissingAndZeroAreDistinctAndFutureCommissioningAgeIsZero()
    {
        var missing = Assert.Single((await analytics.DeviceLifeAsync(new(Day),Ct)).Value!.Items);
        Assert.Null(missing.UtilizationPercent); Assert.Equal("NO_READING",missing.UnavailableReason);
        await Status(device,At(8,1),0); Assert.Equal(0m,Assert.Single((await analytics.DeviceLifeAsync(new(Day),Ct)).Value!.Items).UtilizationPercent);
        var noRating=(await registry.CreateModelAsync(new(Guid.NewGuid(),"厂商","无额定",null,""),Ct)).Value!;
        var d=await Device("B",noRating.Id,new(2027,1,1)); await Status(d,At(8,1),5);
        var row=Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:d),Ct)).Value!.Items);
        Assert.Null(row.UtilizationPercent);Assert.Equal("NO_RATED_LIFE",row.UnavailableReason);Assert.Equal(0,row.AgeDays);
        var noDate=await Device("C",model);Assert.Null(Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:noDate),Ct)).Value!.Items).AgeDays);
    }
    [Fact] public async Task DecimalPercentUsesBankersRoundingAndDoesNotClamp()
    {
        var m=(await registry.CreateModelAsync(new(Guid.NewGuid(),"厂商","精度",20000,""),Ct)).Value!;
        var d=await Device("精度",m.Id);await Status(d,At(8,1),1);
        Assert.Equal(0m,Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:d),Ct)).Value!.Items).UtilizationPercent);
        await Status(d,At(8,2),3);Assert.Equal(.02m,Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:d),Ct)).Value!.Items).UtilizationPercent);
    }
    [Fact] public async Task PagingCountsAllRowsAndReturnsOnlyRequestedSlice()
    {
        for(var i=0;i<52;i++) await Device($"P{i}",model);
        var first=(await analytics.DeviceLifeAsync(new(Day),Ct)).Value!;
        var second=(await analytics.DeviceLifeAsync(new(Day,Page:2),Ct)).Value!;
        Assert.Equal(53,first.TotalCount);Assert.Equal(50,first.Items.Count);Assert.Equal(3,second.Items.Count);Assert.DoesNotContain(second.Items,r=>first.Items.Any(f=>f.DeviceId==r.DeviceId));
        Assert.Empty((await analytics.DeviceLifeAsync(new(Day,Page:int.MaxValue),Ct)).Value!.Items);
    }
    [Fact] public async Task TrendsKeepBeijingMonthBoundaryTicksAndUseExactAverage()
    {
        await Fault(At(9,1).AddTicks(-1),2m); await Fault(At(9,1),3m); await Fault(At(9,2),null,"OTHER","LOW");
        var all=(await analytics.FaultTrendAsync(new(),Ct)).Value!;
        Assert.Equal(new[]{new FaultTrendPoint("2026-08",1),new FaultTrendPoint("2026-09",2)},all.Points);
        Assert.Equal(2m,all.AverageDurationSeconds);Assert.Equal(2,all.ClosedFaultCount);
        var august=(await analytics.FaultTrendAsync(new(From:Day,To:Day),Ct)).Value!;Assert.Equal(1,august.TotalFaults);
        var filter=(await analytics.FaultTrendAsync(new(ModelId:model,DeviceId:device,FaultType:"OTHER",Severity:"LOW"),Ct)).Value!;
        Assert.Equal(1,filter.TotalFaults);Assert.Null(filter.AverageDurationSeconds);
        Assert.Empty((await analytics.FaultTrendAsync(new(FaultType:"OTHER",Severity:"HIGH"),Ct)).Value!.Points);
    }
    [Fact] public async Task DashboardNinetyDaysIncludesFirstDayAndExcludesTomorrow()
    {
        var start=At(8,31).AddDays(-89);await Fault(start.AddTicks(-1));await Fault(start);await Fault(At(9,1).AddTicks(-1));await Fault(At(9,1));
        var inv=new SqliteInventoryService(store);Assert.True((await inv.CreateConsumableAsync(new(Guid.NewGuid(),"空库存","个",""),Ct)).IsSuccess);
        var summary=(await analytics.DashboardAsync(new(Day),Ct)).Value!;
        Assert.Equal(2,summary.RecentFaults.TotalFaults);Assert.Equal(4,summary.OpenFaultCount);Assert.Equal(1,summary.DeviceCount);Assert.Equal(1,summary.ConsumableCount);Assert.Equal(1,summary.ZeroStockCount);
    }
    [Fact] public async Task SimulationExcludedByDefaultAcrossReportsAndExplicitToggleIncludesIt()
    {
        Assert.True((await new SqliteSimulationService(store).CreateAsync(new(Guid.NewGuid(),42),Ct)).IsSuccess);
        Assert.Equal(1,(await analytics.DeviceLifeAsync(new(Day),Ct)).Value!.TotalCount);
        Assert.Equal(25,(await analytics.DeviceLifeAsync(new(Day,IncludeSimulated:true),Ct)).Value!.TotalCount);
        Assert.Equal(0,(await analytics.FaultTrendAsync(new(),Ct)).Value!.TotalFaults);
        Assert.Equal(8,(await analytics.FaultTrendAsync(new(IncludeSimulated:true),Ct)).Value!.TotalFaults);
        var normal=(await analytics.DashboardAsync(new(Day),Ct)).Value!;var all=(await analytics.DashboardAsync(new(Day,true),Ct)).Value!;
        Assert.Equal(1,normal.DeviceCount);Assert.Equal(0,normal.ConsumableCount);Assert.Equal(25,all.DeviceCount);Assert.Equal(4,all.ConsumableCount);Assert.True(all.OpenFaultCount>normal.OpenFaultCount);
    }
    [Fact] public async Task InvalidQueriesFailWithoutThrowingAndCancellationPropagates()
    {
        Assert.False((await analytics.DeviceLifeAsync(new(DateOnly.MaxValue),Ct)).IsSuccess);
        Assert.False((await analytics.FaultTrendAsync(new(From:Day,To:Day.AddDays(-1)),Ct)).IsSuccess);
        Assert.False((await analytics.DashboardAsync(new(DateOnly.MinValue),Ct)).IsSuccess);
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>analytics.DeviceLifeAsync(new(Day),cancel.Token));
    }
    [Fact] public async Task SimulatedLatestStatusDoesNotHideEarlierRealReading()
    {
        var dataset=(await new SqliteSimulationService(store).CreateAsync(new(Guid.NewGuid(),24),Ct)).Value!;
        await Status(device,At(8,1),100,"STOPPED");await Status(device,At(8,2),200);
        await Sql("UPDATE StatusRecord SET Source='SIMULATED',SimulationDatasetId=$dataset WHERE DeviceId=$device AND CumulativeCount=200",("$dataset",dataset.Id),("$device",device));
        var real=Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:device),Ct)).Value!.Items);
        var all=Assert.Single((await analytics.DeviceLifeAsync(new(Day,DeviceId:device,IncludeSimulated:true),Ct)).Value!.Items);
        Assert.Equal(100,real.LatestCount);Assert.Equal("STOPPED",real.LatestStatus);Assert.Equal(200,all.LatestCount);
    }
    [Fact] public async Task ReadReportsPreserveDatabaseBytesAndCanCoexistWithReadonlyConnection()
    {
        await Status(device,At(8,1),100);await Fault(At(8,2),1.5m);
        await store.ReadAsync(async(db,ct)=>{using var q=db.CreateCommand();q.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";await q.ExecuteNonQueryAsync(ct);return true;},Ct);
        var before=System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path,Ct));
        await using(var readOnly=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=path,Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            await readOnly.OpenAsync(Ct);using var tx=readOnly.BeginTransaction(deferred:true);using var q=readOnly.CreateCommand();q.Transaction=tx;q.CommandText="SELECT COUNT(*) FROM Device";Assert.Equal(1L,await q.ExecuteScalarAsync(Ct));
            Assert.True((await analytics.DeviceLifeAsync(new(Day),Ct)).IsSuccess);Assert.True((await analytics.FaultTrendAsync(new(),Ct)).IsSuccess);Assert.True((await analytics.DashboardAsync(new(Day),Ct)).IsSuccess);
        }
        Assert.Equal(before,System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path,Ct)));
        Assert.True(!File.Exists(path+"-wal") || new FileInfo(path+"-wal").Length==0);
    }}
