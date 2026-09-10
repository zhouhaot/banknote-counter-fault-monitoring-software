using Microsoft.Extensions.Logging.Abstractions;
using MoneyCounter.Core;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Registry;
using MoneyCounter.Desktop.ViewModels;
using Xunit;

namespace MoneyCounter.Desktop.Tests;

public sealed class AnalyticsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_UsesExactAssetCode_WhenCaseVariantsExist()
    {
        var registry = new StubRegistry
        {
            Devices = [Device(1, "A"), Device(2, "a")]
        };
        var analytics = new StubAnalytics();
        var vm = new AnalyticsViewModel(analytics, registry, NullLogger<AnalyticsViewModel>.Instance);

        await vm.InitializeAsync();
        vm.AssetCode = "a";
        await vm.RefreshAsync();

        Assert.Equal(2, analytics.LifeQueries.Last().DeviceId);
    }

    [Fact]
    public async Task NextAsync_RefreshesFirstPage_WhenFiltersChanged()
    {
        var registry = new StubRegistry();
        var analytics = new StubAnalytics { LifeTotal = 53 };
        var vm = new AnalyticsViewModel(analytics, registry, NullLogger<AnalyticsViewModel>.Instance);

        await vm.InitializeAsync();
        await vm.NextAsync();
        Assert.Equal(2, analytics.LifeQueries.Last().Page);

        vm.AssetCode = "asset-1";
        registry.Devices = [Device(7, "asset-1")];
        await vm.NextAsync();

        Assert.Equal(1, analytics.LifeQueries.Last().Page);
        Assert.Equal(7, analytics.LifeQueries.Last().DeviceId);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotQueryStatistics_WhenModelLoadCancelled_AndCanRetry()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new StubRegistry { SecondModelPage = gate.Task };
        var analytics = new StubAnalytics();
        var vm = new AnalyticsViewModel(analytics, registry, NullLogger<AnalyticsViewModel>.Instance);

        var loading = vm.InitializeAsync();
        await registry.SecondPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        vm.Cancel();
        await loading.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Empty(analytics.LifeQueries);
        Assert.Equal("查询已取消，请重新查询。", vm.Feedback);

        registry.SecondModelPage = null;
        await vm.RefreshAsync();
        Assert.Single(analytics.LifeQueries);
        Assert.Equal("统计已刷新；概览仅按模拟数据选项筛选，明细和趋势同时应用型号与资产编号。", vm.Feedback);
    }

    private static DeviceDetail Device(long id, string code) => new(id, code, 1, "厂商", "型号", null, null, "位置", "负责人", "", "MANUAL", true, 1, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class StubAnalytics : IAnalyticsService
    {
        public long LifeTotal { get; init; }
        public List<DeviceLifeQuery> LifeQueries { get; } = [];
        public Task<Result<PagedResult<DeviceLifeRow>>> DeviceLifeAsync(DeviceLifeQuery query, CancellationToken cancellationToken = default)
        {
            LifeQueries.Add(query);
            return Task.FromResult(Result<PagedResult<DeviceLifeRow>>.Success(new([], query.Page, query.PageSize, LifeTotal)));
        }
        public Task<Result<FaultTrendReport>> FaultTrendAsync(FaultTrendQuery query, CancellationToken cancellationToken = default) => Task.FromResult(Result<FaultTrendReport>.Success(new([], 0, 0, null)));
        public Task<Result<DashboardSummary>> DashboardAsync(DashboardQuery query, CancellationToken cancellationToken = default) => Task.FromResult(Result<DashboardSummary>.Success(new(0, 0, 0, 0, 0, new([], 0, 0, null))));
    }

    private sealed class StubRegistry : IRegistryService
    {
        public IReadOnlyList<DeviceDetail> Devices { get; set; } = [];
        public Task? SecondModelPage { get; set; }
        public TaskCompletionSource SecondPageRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result<ModelDetail>> CreateModelAsync(CreateModelCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<ModelDetail>> UpdateModelAsync(UpdateModelCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<ModelDetail>> GetModelAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<Result<PagedResult<ModelDetail>>> ListModelsAsync(ModelQuery query, CancellationToken cancellationToken = default)
        {
            if (query.Page == 2 && SecondModelPage is { } pending) { SecondPageRequested.TrySetResult(); await pending.WaitAsync(cancellationToken); }
            var total = SecondModelPage is null ? 1 : 51;
            IReadOnlyList<ModelDetail> items = query.Page == 1 ? [new ModelDetail(1, "厂商", "型号", null, "", "MANUAL", true, 1, DateTime.UtcNow, DateTime.UtcNow)] : [];
            return Result<PagedResult<ModelDetail>>.Success(new(items, query.Page, 50, total));
        }
        public Task<Result<ModelDetail>> DeactivateModelAsync(DeactivateCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<DeviceDetail>> CreateDeviceAsync(CreateDeviceCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<DeviceDetail>> UpdateDeviceAsync(UpdateDeviceCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<DeviceDetail>> GetDeviceAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<PagedResult<DeviceDetail>>> ListDevicesAsync(DeviceQuery query, CancellationToken cancellationToken = default)
        {
            var filtered = string.IsNullOrEmpty(query.Search) ? Devices : Devices.Where(x => x.AssetCode.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult(Result<PagedResult<DeviceDetail>>.Success(new(filtered, query.Page, query.PageSize, filtered.Count())));
        }
        public Task<Result<DeviceDetail>> DeactivateDeviceAsync(DeactivateCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
