using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Imports;
using MoneyCounter.Core.Registry;
using MoneyCounter.Infrastructure.Analytics;
using MoneyCounter.Infrastructure.Imports;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Analytics;

public sealed class AnalyticsPerformanceTests
{
    public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable("MONEYCOUNTER_BENCHMARK"), "1", StringComparison.Ordinal);

    [Fact(Skip = "Set MONEYCOUNTER_BENCHMARK=1 to run the 500-device, 100000-status, 10000-row CSV performance probe.", SkipUnless = nameof(IsEnabled))]
    public async Task AnalyticsAndCsv_RecordsMeasuredResults_AtPlannedScale()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "moneycounter-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "benchmark.sqlite3");
        var csvPath = Path.Combine(root, "models-10000.csv");
        try
        {
            var setup = Stopwatch.StartNew();
            await using var store = new DbStore(databasePath);
            await store.InitializeAsync(ct);
            var registry = new SqliteRegistryService(store);
            var model = (await registry.CreateModelAsync(new(Guid.NewGuid(), "性能厂商", "性能基准型号", 1_000_000, ""), ct)).Value!;
            await SeedAsync(store, model.Id, ct);
            setup.Stop();

            var analytics = new SqliteAnalyticsService(store);
            List<long> pageMilliseconds = [];
            for (var sample = 0; sample < 20; sample++)
            {
                var page = sample % 10 + 1;
                var timer = Stopwatch.StartNew();
                var result = await analytics.DeviceLifeAsync(new(new DateOnly(2026, 9, 10), page, 50), ct);
                timer.Stop();
                Assert.True(result.IsSuccess);
                pageMilliseconds.Add(timer.ElapsedMilliseconds);
            }
            var trendTimer = Stopwatch.StartNew();
            Assert.True((await analytics.FaultTrendAsync(new(), ct)).IsSuccess);
            trendTimer.Stop();
            var dashboardTimer = Stopwatch.StartNew();
            Assert.True((await analytics.DashboardAsync(new(new DateOnly(2026, 9, 10)), ct)).IsSuccess);
            dashboardTimer.Stop();

            var csvTimer = Stopwatch.StartNew();
            await File.WriteAllTextAsync(csvPath, BuildCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
            var importer = new SqliteCsvImportService(store);
            var preview = await importer.PreviewAsync(ImportKind.Model, csvPath, ct);
            Assert.True(preview.IsSuccess);
            Assert.True(preview.Value!.CanCommit);
            csvTimer.Stop();
            var commitTimer = Stopwatch.StartNew();
            var commit = await importer.CommitAsync(Guid.NewGuid(), ImportKind.Model, csvPath, preview.Value!.Sha256, ct);
            commitTimer.Stop();
            Assert.True(commit.IsSuccess);
            Assert.True(commit.Value!.IsCommitted);

            var report = new
            {
                machine = Environment.MachineName,
                os = Environment.OSVersion.VersionString,
                framework = Environment.Version.ToString(),
                recordedAtUtc = DateTimeOffset.UtcNow,
                scale = new { devices = 500, statusRecords = 100000, csvRows = 10000 },
                milliseconds = new
                {
                    dataSetup = setup.ElapsedMilliseconds,
                    lifePageMin = pageMilliseconds.Min(),
                    lifePageP95 = Percentile(pageMilliseconds, 0.95),
                    lifePageMax = pageMilliseconds.Max(),
                    faultTrend = trendTimer.ElapsedMilliseconds,
                    dashboard = dashboardTimer.ElapsedMilliseconds,
                    csvPreviewAndWrite = csvTimer.ElapsedMilliseconds,
                    csvCommit = commitTimer.ElapsedMilliseconds
                },
                notes = "Cold-start, UI responsiveness, memory, high-DPI and multi-monitor measurements are recorded separately; this probe measures isolated SQLite service calls."
            };
            var evidencePath = Environment.GetEnvironmentVariable("MONEYCOUNTER_BENCHMARK_EVIDENCE") ?? Path.Combine(root, "analytics-performance.json");
            var parent = Path.GetDirectoryName(Path.GetFullPath(evidencePath))!;
            Directory.CreateDirectory(parent);
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedAsync(DbStore store, long modelId, CancellationToken ct) => await store.WriteAsync(async (db, tx, token) =>
    {
        List<long> devices = [];
        for (var device = 0; device < 500; device++)
        {
            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Device(AssetCode,ModelId,CommissionedOn,PurchasedOn,Location,ResponsiblePerson,Notes,IsActive,Revision,Source,CreatedAtUtc,UpdatedAtUtc) VALUES($code,$model,'2020-01-01',NULL,'性能','','',1,0,'MANUAL',$now,$now); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$code", $"PERF-{device:D3}");
            insert.Parameters.AddWithValue("$model", modelId);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            devices.Add(Convert.ToInt64(await insert.ExecuteScalarAsync(token)));
        }
        using var status = db.CreateCommand();
        status.Transaction = tx;
        status.CommandText = "INSERT INTO StatusRecord(DeviceId,RecordedAt,Status,CumulativeCount,Notes,Source,CreatedAtUtc) VALUES($device,$at,'RUNNING',$count,'','MANUAL',$now)";
        var deviceParameter = status.Parameters.Add("$device", SqliteType.Integer);
        var timeParameter = status.Parameters.Add("$at", SqliteType.Text);
        var countParameter = status.Parameters.Add("$count", SqliteType.Integer);
        var nowParameter = status.Parameters.Add("$now", SqliteType.Text);
        nowParameter.Value = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        await status.PrepareAsync(token);
        var anchor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var row = 0; row < 100_000; row++)
        {
            deviceParameter.Value = devices[row % devices.Count];
            timeParameter.Value = anchor.AddMinutes(row / devices.Count).UtcDateTime.ToString("O");
            countParameter.Value = row / devices.Count;
            await status.ExecuteNonQueryAsync(token);
        }
        return true;
    }, ct);

    private static string BuildCsv()
    {
        var csv = new StringBuilder("manufacturer,model_name,rated_count_life,notes\r\n");
        for (var row = 0; row < 10_000; row++) csv.Append("CSV 性能厂商,CSV-PERF-").Append(row.ToString("D5")).Append(",1000000,性能导入\r\n");
        return csv.ToString();
    }

    private static long Percentile(IReadOnlyList<long> values, double percentile) => values.OrderBy(x => x).ElementAt((int)Math.Ceiling(values.Count * percentile) - 1);
}
