namespace MoneyCounter.Core.Operations;

public static class DeviceStates
{
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[] { "RUNNING", "STOPPED", "FAULT", "MAINTENANCE", "RETIRED" });
}
public sealed record RecordStatusCommand(Guid OperationId, long DeviceId, DateTimeOffset RecordedAt, string Status, long CumulativeCount, string Notes);
public sealed record CreateAnomalyCommand(Guid OperationId, long DeviceId, DateTimeOffset DiscoveredAt, string Description);
public sealed record CloseAnomalyCommand(Guid OperationId, long Id, long ExpectedRevision, DateTimeOffset ClosedAt, string HandlingNotes);
public sealed record StatusQuery(int Page = 1, int PageSize = 50, long? DeviceId = null, string? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null);
public sealed record AnomalyQuery(int Page = 1, int PageSize = 50, long? DeviceId = null, string? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null);
public sealed record StatusDetail(long Id, long DeviceId, string AssetCode, DateTimeOffset RecordedAt, string Status, long CumulativeCount, long? Increment, string Notes, string Source);
public sealed record AnomalyDetail(long Id, long DeviceId, string AssetCode, DateTimeOffset DiscoveredAt, string Description, string Status, string HandlingNotes, DateTimeOffset? ClosedAt, long Revision, string Source);
public interface IOperationsService
{
    Task<Result<StatusDetail>> RecordStatusAsync(RecordStatusCommand command, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<StatusDetail>>> ListStatusesAsync(StatusQuery query, CancellationToken cancellationToken = default);
    Task<Result<StatusDetail?>> LatestStatusAsync(long deviceId, CancellationToken cancellationToken = default);
    Task<Result<AnomalyDetail>> CreateAnomalyAsync(CreateAnomalyCommand command, CancellationToken cancellationToken = default);
    Task<Result<AnomalyDetail>> CloseAnomalyAsync(CloseAnomalyCommand command, CancellationToken cancellationToken = default);
    Task<Result<AnomalyDetail>> GetAnomalyAsync(long id, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<AnomalyDetail>>> ListAnomaliesAsync(AnomalyQuery query, CancellationToken cancellationToken = default);
}
