using MoneyCounter.Core;
using MoneyCounter.Core.Registry;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Registry;

public sealed class RegistryServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"money-counter-{Guid.NewGuid():N}.db");
    private DbStore _store = null!;
    private IRegistryService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new DbStore(_path);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _service = new SqliteRegistryService(_store);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        File.Delete(_path);
    }

    [Fact]
    public async Task DuplicateModelIdentityReturnsStableFieldError()
    {
        await CreateModelAsync("康艺", "JBYD-01");

        var result = await CreateModelAsync("康艺", "JBYD-01");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.DuplicateRecord, result.Error!.Code);
        Assert.Equal("ModelName", result.Error.Field);
    }

    [Fact]
    public async Task SameOperationAndPayloadReturnsOriginalReceipt()
    {
        var operationId = Guid.NewGuid();
        var command = new CreateModelCommand(operationId, "康艺", "JBYD-02", 1000, "");

        var first = await _service.CreateModelAsync(command, TestContext.Current.CancellationToken);
        var retry = await _service.CreateModelAsync(command, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.Equal(first.Value!.Id, retry.Value!.Id);
    }

    [Fact]
    public async Task RetryReturnsOriginalSnapshotAfterEntityChanges()
    {
        var operationId = Guid.NewGuid();
        var command = new CreateModelCommand(operationId, "康艺", "JBYD-snapshot", 1000, "original");
        var first = (await _service.CreateModelAsync(command, TestContext.Current.CancellationToken)).Value!;
        Assert.True((await _service.UpdateModelAsync(new(Guid.NewGuid(), first.Id, first.Revision, "康艺", "JBYD-snapshot", 2000, "changed"), TestContext.Current.CancellationToken)).IsSuccess);

        var retry = (await _service.CreateModelAsync(command, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal("original", retry.Notes);
        Assert.Equal(1000, retry.RatedCountLife);
    }

    [Fact]
    public async Task ReusingOperationWithDifferentPayloadIsRejected()
    {
        var operationId = Guid.NewGuid();
        await _service.CreateModelAsync(new(operationId, "A", "M1", 1, ""), TestContext.Current.CancellationToken);

        var result = await _service.CreateModelAsync(new(operationId, "A", "M2", 1, ""), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRecord, result.Error!.Code);
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteModel()
    {
        var created = (await CreateModelAsync("A", "M")).Value!;
        var updated = await _service.UpdateModelAsync(new(Guid.NewGuid(), created.Id, created.Revision, "A", "M", 200, "new"), TestContext.Current.CancellationToken);

        var stale = await _service.UpdateModelAsync(new(Guid.NewGuid(), created.Id, created.Revision, "A", "M", 300, "stale"), TestContext.Current.CancellationToken);

        Assert.True(updated.IsSuccess);
        Assert.False(stale.IsSuccess);
        Assert.Equal(ErrorCodes.ConcurrentChange, stale.Error!.Code);
    }

    [Fact]
    public async Task DeviceRequiresExistingActiveModelAndUniqueAssetCode()
    {
        var model = (await CreateModelAsync("A", "M")).Value!;
        var first = await CreateDeviceAsync("ZC-001", model.Id);
        var duplicate = await CreateDeviceAsync("ZC-001", model.Id);
        var missing = await CreateDeviceAsync("ZC-002", long.MaxValue);

        Assert.True(first.IsSuccess);
        Assert.Equal(ErrorCodes.DuplicateRecord, duplicate.Error!.Code);
        Assert.Equal(ErrorCodes.RecordNotFound, missing.Error!.Code);
    }

    [Fact]
    public async Task ReferencedModelCannotBeDeactivated()
    {
        var model = (await CreateModelAsync("A", "InUse")).Value!;
        Assert.True((await CreateDeviceAsync("ZC-in-use", model.Id)).IsSuccess);

        var result = await _service.DeactivateModelAsync(new(Guid.NewGuid(), model.Id, model.Revision), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRecord, result.Error!.Code);
    }

    [Fact]
    public async Task ListsArePagedAndDetailsSurviveReopen()
    {
        for (var i = 0; i < 55; i++) await CreateModelAsync("厂商", $"型号{i:00}");

        var firstPage = await _service.ListModelsAsync(new(Page: 1, PageSize: 50), TestContext.Current.CancellationToken);
        Assert.Equal(50, firstPage.Value!.Items.Count);
        Assert.Equal(55, firstPage.Value.TotalCount);
        var id = firstPage.Value.Items[0].Id;

        await _store.DisposeAsync();
        _store = new DbStore(_path);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
        _service = new SqliteRegistryService(_store);
        Assert.True((await _service.GetModelAsync(id, TestContext.Current.CancellationToken)).IsSuccess);
    }

    private Task<Result<ModelDetail>> CreateModelAsync(string manufacturer, string modelName) =>
        _service.CreateModelAsync(new(Guid.NewGuid(), manufacturer, modelName, 1000, ""), TestContext.Current.CancellationToken);

    private Task<Result<DeviceDetail>> CreateDeviceAsync(string assetCode, long modelId) =>
        _service.CreateDeviceAsync(new(Guid.NewGuid(), assetCode, modelId, null, null, "", "", ""), TestContext.Current.CancellationToken);
}
