namespace MoneyCounter.Core.Analytics;

public sealed record DeviceLifeQuery(DateOnly AsOfDate, int Page = 1, int PageSize = 50, long? ModelId = null, long? DeviceId = null, string? LatestStatus = null, bool IncludeSimulated = false);
public sealed record DeviceLifeRow(long DeviceId, string AssetCode, long ModelId, string ModelName, long? LatestCount, string? LatestStatus, DateTimeOffset? LatestRecordedAt, long? RatedCountLife, decimal? UtilizationPercent, string? UnavailableReason, int? AgeDays, string Source);
public sealed record FaultTrendQuery(DateOnly? From = null, DateOnly? To = null, long? ModelId = null, long? DeviceId = null, string? FaultType = null, string? Severity = null, bool IncludeSimulated = false);
public sealed record FaultTrendPoint(string Month, long Count);
public sealed record FaultTrendReport(IReadOnlyList<FaultTrendPoint> Points, long TotalFaults, long ClosedFaultCount, decimal? AverageDurationSeconds);
public sealed record DashboardQuery(DateOnly AsOfDate, bool IncludeSimulated = false);
public sealed record DashboardSummary(long DeviceCount, long OpenAnomalyCount, long OpenFaultCount, long ConsumableCount, long ZeroStockCount, FaultTrendReport RecentFaults);
public interface IAnalyticsService
{
    Task<Result<PagedResult<DeviceLifeRow>>> DeviceLifeAsync(DeviceLifeQuery query, CancellationToken cancellationToken = default);
    Task<Result<FaultTrendReport>> FaultTrendAsync(FaultTrendQuery query, CancellationToken cancellationToken = default);
    Task<Result<DashboardSummary>> DashboardAsync(DashboardQuery query, CancellationToken cancellationToken = default);
}
