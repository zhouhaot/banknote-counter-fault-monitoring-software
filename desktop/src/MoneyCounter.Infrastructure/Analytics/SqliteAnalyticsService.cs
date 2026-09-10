using System.Globalization;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Core.Operations;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Infrastructure.Analytics;

public sealed class SqliteAnalyticsService(DbStore store) : IAnalyticsService
{
    private static readonly DateOnly EarliestDate = new(1, 1, 2);
    private static bool ValidDate(DateOnly day) => day >= EarliestDate && day < DateOnly.MaxValue;
    private static string Boundary(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(8)).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(string stamp) => DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture);
    private static Result<T> Invalid<T>() => Result<T>.Failure(ErrorCodes.InvalidRecord, "统计日期、分页或筛选条件无效。", "Query");
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private async Task<Result<T>> Read<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action, CancellationToken token)
    {
        try
        {
            return await store.ReadAsync(async (db, ct) =>
            {
                using var tx = db.BeginTransaction(deferred: true);
                var result = await action(db, tx, ct);
                return Result<T>.Success(result);
            }, token);
        }
        catch (SqliteException) { return Result<T>.Failure(ErrorCodes.StorageUnavailable, "无法读取统计数据，请稍后重试。"); }
    }

    public Task<Result<PagedResult<DeviceLifeRow>>> DeviceLifeAsync(DeviceLifeQuery query, CancellationToken cancellationToken = default)
    {
        if (!ValidDate(query.AsOfDate) || query.Page < 1 || query.PageSize is < 1 or > 200 || query.ModelId is <= 0 || query.DeviceId is <= 0 || query.LatestStatus is not null && !DeviceStates.All.Contains(query.LatestStatus))
            return Task.FromResult(Invalid<PagedResult<DeviceLifeRow>>());
        return Read(async (db, tx, ct) =>
        {
            const string from = """
                FROM Device d JOIN Model m ON m.Id=d.ModelId
                LEFT JOIN StatusRecord s ON s.Id=(SELECT r.Id FROM StatusRecord r WHERE r.DeviceId=d.Id AND r.RecordedAt<$cutoff AND ($sim=1 OR r.Source<>'SIMULATED') ORDER BY r.RecordedAt DESC,r.Id DESC LIMIT 1)
                WHERE ($sim=1 OR d.Source<>'SIMULATED') AND ($model IS NULL OR d.ModelId=$model) AND ($device IS NULL OR d.Id=$device) AND ($status IS NULL OR s.Status=$status)
                """;
            (string, object?)[] values = [("$cutoff", Boundary(query.AsOfDate.AddDays(1))), ("$sim", query.IncludeSimulated), ("$model", query.ModelId), ("$device", query.DeviceId), ("$status", query.LatestStatus), ("$limit", query.PageSize), ("$offset", ((long)query.Page - 1) * query.PageSize)];
            using var count = Command(db, tx, "SELECT COUNT(*) " + from, values);
            var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            using var select = Command(db, tx, "SELECT d.Id,d.AssetCode,m.Id,m.ModelName,s.CumulativeCount,s.Status,s.RecordedAt,m.RatedCountLife,d.CommissionedOn,d.Source " + from + " ORDER BY d.Id LIMIT $limit OFFSET $offset", values);
            using var reader = await select.ExecuteReaderAsync(ct);
            List<DeviceLifeRow> rows = [];
            while (await reader.ReadAsync(ct))
            {
                long? reading = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                long? rated = reader.IsDBNull(7) ? null : reader.GetInt64(7);
                var reason = reading is null ? "NO_READING" : rated is null or <= 0 ? "NO_RATED_LIFE" : null;
                decimal? percent = reason is null ? decimal.Round((decimal)reading!.Value / rated!.Value * 100m, 2, MidpointRounding.ToEven) : null;
                int? age = reader.IsDBNull(8) ? null : Math.Max(0, query.AsOfDate.DayNumber - DateOnly.Parse(reader.GetString(8), CultureInfo.InvariantCulture).DayNumber);
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reading, reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : Time(reader.GetString(6)), rated, percent, reason, age, reader.GetString(9)));
            }
            return new PagedResult<DeviceLifeRow>(rows.AsReadOnly(), query.Page, query.PageSize, total);
        }, cancellationToken);
    }
    private static bool ValidTrend(FaultTrendQuery query) => !(query.From.HasValue && !ValidDate(query.From.Value) || query.To.HasValue && !ValidDate(query.To.Value) || query.From > query.To || query.ModelId is <= 0 || query.DeviceId is <= 0 || query.FaultType is not null && !FaultValues.Types.Contains(query.FaultType) || query.Severity is not null && !FaultValues.Severities.Contains(query.Severity));
    public Task<Result<FaultTrendReport>> FaultTrendAsync(FaultTrendQuery query, CancellationToken cancellationToken = default) => !ValidTrend(query) ? Task.FromResult(Invalid<FaultTrendReport>()) : Read((db, tx, ct) => Trend(db, tx, query, ct), cancellationToken);

    private static async Task<FaultTrendReport> Trend(SqliteConnection db, SqliteTransaction tx, FaultTrendQuery query, CancellationToken ct)
    {
        // Add only active predicates so SQLite can seek IX_Fault_Time for bounded periods.
        List<string> predicates = ["1=1"];
        if (!query.IncludeSimulated) predicates.Add("f.Source<>'SIMULATED'");
        if (query.From.HasValue) predicates.Add("f.RegisteredAt>=$from");
        if (query.To.HasValue) predicates.Add("f.RegisteredAt<$to");
        if (query.ModelId.HasValue) predicates.Add("d.ModelId=$model");
        if (query.DeviceId.HasValue) predicates.Add("f.DeviceId=$device");
        if (query.FaultType is not null) predicates.Add("f.FaultType=$type");
        if (query.Severity is not null) predicates.Add("f.Severity=$severity");
        var from = "FROM Fault f JOIN Device d ON d.Id=f.DeviceId WHERE " + string.Join(" AND ", predicates);
        (string, object?)[] values = [("$sim", query.IncludeSimulated), ("$from", query.From.HasValue ? Boundary(query.From.Value) : null), ("$to", query.To.HasValue ? Boundary(query.To.Value.AddDays(1)) : null), ("$model", query.ModelId), ("$device", query.DeviceId), ("$type", query.FaultType), ("$severity", query.Severity)];
        List<FaultTrendPoint> points = [];
        // Strip subsecond digits before SQLite month conversion: SQLite rounds .9999999 into the next second.
        using (var command = Command(db, tx, "SELECT strftime('%Y-%m',substr(f.RegisteredAt,1,19),'+8 hours'),COUNT(*) " + from + " GROUP BY 1 ORDER BY 1", values))
        using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) points.Add(new(reader.GetString(0), reader.GetInt64(1)));
        decimal ticks = 0; long closed = 0;
        // Stream a single query to sum decimal ticks exactly, avoiding both Int64 SQL SUM overflow and julianday floating point error.
        using (var command = Command(db, tx, "SELECT f.RegisteredAt,f.ClosedAt " + from + " AND f.Status='CLOSED' AND f.ClosedAt IS NOT NULL AND f.ClosedAt>=f.RegisteredAt", values))
        using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) { ticks += (Time(reader.GetString(1)) - Time(reader.GetString(0))).Ticks; closed++; }
        return new(points.AsReadOnly(), points.Sum(p => p.Count), closed, closed == 0 ? null : decimal.Round(ticks / TimeSpan.TicksPerSecond / closed, 0, MidpointRounding.ToEven));
    }
    public Task<Result<DashboardSummary>> DashboardAsync(DashboardQuery query, CancellationToken cancellationToken = default)
    {
        if (!ValidDate(query.AsOfDate) || query.AsOfDate.DayNumber < EarliestDate.DayNumber + 89) return Task.FromResult(Invalid<DashboardSummary>());
        return Read(async (db, tx, ct) =>
        {
            const string sql = """
                SELECT (SELECT COUNT(*) FROM Device WHERE $sim=1 OR Source<>'SIMULATED'),
                (SELECT COUNT(*) FROM Anomaly WHERE Status='OPEN' AND ($sim=1 OR Source<>'SIMULATED')),
                (SELECT COUNT(*) FROM Fault WHERE Status IN ('PENDING','IN_PROGRESS') AND ($sim=1 OR Source<>'SIMULATED')),
                (SELECT COUNT(*) FROM Consumable WHERE $sim=1 OR Source<>'SIMULATED'),
                (SELECT COUNT(*) FROM Consumable c WHERE ($sim=1 OR c.Source<>'SIMULATED') AND COALESCE((SELECT SUM(i.QuantityMinor) FROM InventoryMovement i WHERE i.ConsumableId=c.Id AND ($sim=1 OR i.Source<>'SIMULATED')),0)<=0)
                """;
            long devices, anomalies, faults, consumables, zero;
            using (var command = Command(db, tx, sql, ("$sim", query.IncludeSimulated)))
            using (var reader = await command.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct); devices = reader.GetInt64(0); anomalies = reader.GetInt64(1); faults = reader.GetInt64(2); consumables = reader.GetInt64(3); zero = reader.GetInt64(4);
            }
            var recent = await Trend(db, tx, new(query.AsOfDate.AddDays(-89), query.AsOfDate, IncludeSimulated: query.IncludeSimulated), ct);
            return new DashboardSummary(devices, anomalies, faults, consumables, zero, recent);
        }, cancellationToken);
    }
}
