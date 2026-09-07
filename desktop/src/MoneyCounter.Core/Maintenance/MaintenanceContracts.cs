namespace MoneyCounter.Core.Maintenance;

public static class FaultValues
{
    public static readonly IReadOnlyList<string> Types = Array.AsReadOnly(new[] { "CARD_JAM", "COUNTING", "AUTHENTICATION", "FEED", "DISPLAY", "POWER", "MECHANICAL", "OTHER" });
    public static readonly IReadOnlyList<string> Severities = Array.AsReadOnly(new[] { "LOW", "MEDIUM", "HIGH", "URGENT" });
    public static readonly IReadOnlyList<string> Statuses = Array.AsReadOnly(new[] { "PENDING", "IN_PROGRESS", "CLOSED" });
}
public sealed record CreateFaultCommand(Guid OperationId, long DeviceId, DateTimeOffset RegisteredAt, string FaultType, string Severity, string Description);
public sealed record ConvertAnomalyCommand(Guid OperationId, long AnomalyId, long ExpectedRevision, DateTimeOffset RegisteredAt, string FaultType, string Severity, string Description);
public sealed record StartFaultCommand(Guid OperationId, long Id, long ExpectedRevision, DateTimeOffset StartedAt);
public sealed record CloseFaultCommand(Guid OperationId, long Id, long ExpectedRevision, DateTimeOffset ClosedAt, string FinalResult);
public sealed record AddRepairCommand(Guid OperationId, long FaultId, long ExpectedFaultRevision, DateTimeOffset RepairedAt, string Action, string Result, string Technician, string Notes);
public sealed record UpdateRepairCommand(Guid OperationId, long Id, long ExpectedFaultRevision, DateTimeOffset RepairedAt, string Action, string Result, string Technician, string Notes);
public sealed record DeleteRepairCommand(Guid OperationId, long Id, long ExpectedFaultRevision);
public sealed record FaultQuery(int Page = 1, int PageSize = 50, long? DeviceId = null, string? Status = null, string? FaultType = null, string? Severity = null, DateTimeOffset? From = null, DateTimeOffset? To = null);
public sealed record FaultDetail(long Id, string FaultNo, long DeviceId, string AssetCode, long? SourceAnomalyId, DateTimeOffset RegisteredAt, string FaultType, string Severity, string Description, string Status, DateTimeOffset? StartedAt, DateTimeOffset? ClosedAt, string FinalResult, long Revision, string Source);
public sealed record RepairDetail(long Id, long FaultId, DateTimeOffset RepairedAt, string Action, string Result, string Technician, string Notes, string Source);
public interface IMaintenanceService
{
    Task<Result<FaultDetail>> CreateFaultAsync(CreateFaultCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> ConvertAnomalyAsync(ConvertAnomalyCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> StartFaultAsync(StartFaultCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> CloseFaultAsync(CloseFaultCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> AddRepairAsync(AddRepairCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> UpdateRepairAsync(UpdateRepairCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> DeleteRepairAsync(DeleteRepairCommand command, CancellationToken cancellationToken = default);
    Task<Result<FaultDetail>> GetFaultAsync(long id, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<FaultDetail>>> ListFaultsAsync(FaultQuery query, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<RepairDetail>>> ListRepairsAsync(long faultId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
}
