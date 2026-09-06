namespace MoneyCounter.Core.Registry;

public sealed record CreateModelCommand(Guid OperationId, string Manufacturer, string ModelName, long? RatedCountLife, string Notes);
public sealed record UpdateModelCommand(Guid OperationId, long Id, long ExpectedRevision, string Manufacturer, string ModelName, long? RatedCountLife, string Notes);
public sealed record CreateDeviceCommand(Guid OperationId, string AssetCode, long ModelId, DateOnly? CommissionedOn, DateOnly? PurchasedOn, string Location, string ResponsiblePerson, string Notes);
public sealed record UpdateDeviceCommand(Guid OperationId, long Id, long ExpectedRevision, string AssetCode, long ModelId, DateOnly? CommissionedOn, DateOnly? PurchasedOn, string Location, string ResponsiblePerson, string Notes);
public sealed record DeactivateCommand(Guid OperationId, long Id, long ExpectedRevision);

public sealed record ModelQuery(int Page = 1, int PageSize = 50, string? Search = null, bool? IsActive = null);
public sealed record DeviceQuery(int Page = 1, int PageSize = 50, string? Search = null, bool? IsActive = null, long? ModelId = null);

public sealed record ModelDetail(long Id, string Manufacturer, string ModelName, long? RatedCountLife, string Notes, string Source, bool IsActive, long Revision, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record DeviceDetail(long Id, string AssetCode, long ModelId, string Manufacturer, string ModelName, DateOnly? CommissionedOn, DateOnly? PurchasedOn, string Location, string ResponsiblePerson, string Notes, string Source, bool IsActive, long Revision, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public interface IRegistryService
{
    Task<Result<ModelDetail>> CreateModelAsync(CreateModelCommand command, CancellationToken cancellationToken = default);
    Task<Result<ModelDetail>> UpdateModelAsync(UpdateModelCommand command, CancellationToken cancellationToken = default);
    Task<Result<ModelDetail>> GetModelAsync(long id, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<ModelDetail>>> ListModelsAsync(ModelQuery query, CancellationToken cancellationToken = default);
    Task<Result<ModelDetail>> DeactivateModelAsync(DeactivateCommand command, CancellationToken cancellationToken = default);
    Task<Result<DeviceDetail>> CreateDeviceAsync(CreateDeviceCommand command, CancellationToken cancellationToken = default);
    Task<Result<DeviceDetail>> UpdateDeviceAsync(UpdateDeviceCommand command, CancellationToken cancellationToken = default);
    Task<Result<DeviceDetail>> GetDeviceAsync(long id, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<DeviceDetail>>> ListDevicesAsync(DeviceQuery query, CancellationToken cancellationToken = default);
    Task<Result<DeviceDetail>> DeactivateDeviceAsync(DeactivateCommand command, CancellationToken cancellationToken = default);
}
