namespace MoneyCounter.Core.Inventory;

public static class InventoryValues
{
    public const decimal MaximumQuantity = 9999999999.99m;
    public static readonly IReadOnlyList<string> MovementTypes = Array.AsReadOnly(new[] { "INBOUND", "ISSUE", "RETURN", "ADJUST" });
}
public sealed record CreateConsumableCommand(Guid OperationId, string Name, string Unit, string Notes);
public sealed record UpdateConsumableCommand(Guid OperationId, long Id, long ExpectedRevision, string Name, string Unit, string Notes);
public sealed record DeactivateConsumableCommand(Guid OperationId, long Id, long ExpectedRevision);
public sealed record PostMovementCommand(Guid OperationId, long ConsumableId, string MovementType, decimal Quantity, DateTimeOffset OccurredAt, string Reason, long? DeviceId = null, long? FaultId = null);
public sealed record ReverseMovementCommand(Guid OperationId, long Id, DateTimeOffset OccurredAt, string Reason);
public sealed record ConsumableQuery(int Page = 1, int PageSize = 50, string? Search = null, bool? IsActive = null);
public sealed record MovementQuery(int Page = 1, int PageSize = 50, long? ConsumableId = null, long? DeviceId = null, long? FaultId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);
public sealed record ConsumableDetail(long Id, string Name, string Unit, string Notes, bool IsActive, long Revision, string Source, long StockMinor)
{
    public decimal CurrentStock => StockMinor / 100m;
}
public sealed record MovementDetail(long Id, long ConsumableId, string ConsumableName, string Unit, string MovementType, long QuantityMinor, DateTimeOffset OccurredAt, string Reason, long? DeviceId, string? AssetCode, long? FaultId, long? ReversesId, bool HasDirectReversal, string Source)
{
    public decimal Quantity => QuantityMinor / 100m;
}
public interface IInventoryService
{
    Task<Result<ConsumableDetail>> CreateConsumableAsync(CreateConsumableCommand command, CancellationToken cancellationToken = default);
    Task<Result<ConsumableDetail>> UpdateConsumableAsync(UpdateConsumableCommand command, CancellationToken cancellationToken = default);
    Task<Result<ConsumableDetail>> DeactivateConsumableAsync(DeactivateConsumableCommand command, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<ConsumableDetail>>> ListConsumablesAsync(ConsumableQuery query, CancellationToken cancellationToken = default);
    Task<Result<MovementDetail>> PostMovementAsync(PostMovementCommand command, CancellationToken cancellationToken = default);
    Task<Result<MovementDetail>> ReverseMovementAsync(ReverseMovementCommand command, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<MovementDetail>>> ListMovementsAsync(MovementQuery query, CancellationToken cancellationToken = default);
}
