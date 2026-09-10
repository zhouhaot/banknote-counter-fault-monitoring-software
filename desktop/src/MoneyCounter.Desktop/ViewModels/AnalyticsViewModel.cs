using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Registry;

namespace MoneyCounter.Desktop.ViewModels;

public sealed record AnalyticsChoice(long? Id, string Label);
public sealed record TrendDisplay(string Month, long Count, double BarWidth);
public sealed record LifeDisplay(DeviceLifeRow Row)
{
    public string Count => Row.LatestCount?.ToString("N0") ?? "未提供";
    public string Rated => Row.RatedCountLife?.ToString("N0") ?? "未提供";
    public string Utilization => Row.UtilizationPercent is { } ratio ? $"{ratio:N2}%" : "不可计算";
    public string Age => Row.AgeDays is { } days ? $"{days} 天" : "未提供";
    public string Status => OperationsViewModel.States.FirstOrDefault(x => x.Code == Row.LatestStatus)?.Label ?? "未提供";
}

public partial class AnalyticsViewModel(IAnalyticsService service, IRegistryService registry, ILogger<AnalyticsViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private int _page = 1;
    private long _total;
    private bool _filtersChanged = true;
    private bool _initialized;
    public ObservableCollection<AnalyticsChoice> Models { get; } = [new(null, "全部型号")];
    public ObservableCollection<LifeDisplay> LifeRows { get; } = [];
    public ObservableCollection<TrendDisplay> Trend { get; } = [];
    public IReadOnlyList<StateOption> States { get; } = [new("", "全部状态"), .. OperationsViewModel.States];
    public IReadOnlyList<StateOption> FaultTypes { get; } = [new("", "全部故障类型"), .. MaintenanceViewModel.Types];
    public IReadOnlyList<StateOption> Severities { get; } = [new("", "全部严重程度"), .. MaintenanceViewModel.Severities];
    [ObservableProperty] private string selectedState = "";
    [ObservableProperty] private string selectedFaultType = "";
    [ObservableProperty] private string selectedSeverity = "";
    [ObservableProperty] private AnalyticsChoice? selectedModel;
    [ObservableProperty] private string assetCode = "";
    [ObservableProperty] private DateTime? asOf = DateTime.UtcNow.AddHours(8).Date;
    [ObservableProperty] private DateTime? trendFrom = DateTime.UtcNow.AddHours(8).Date.AddDays(-89);
    [ObservableProperty] private DateTime? trendTo = DateTime.UtcNow.AddHours(8).Date;
    [ObservableProperty] private bool includeSimulated;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string feedback = "统计按北京时间日期计算。";
    [ObservableProperty] private string dashboardText = "尚未查询";
    [ObservableProperty] private string trendSummary = "尚未查询";
    public bool IsReady => !IsBusy;
    public string PageInfo => $"第 {_page} 页，共 {_total} 台设备";
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));
    partial void OnSelectedModelChanged(AnalyticsChoice? value) => _filtersChanged = true;
    partial void OnAssetCodeChanged(string value) => _filtersChanged = true;
    partial void OnAsOfChanged(DateTime? value) => _filtersChanged = true;
    partial void OnTrendFromChanged(DateTime? value) => _filtersChanged = true;
    partial void OnTrendToChanged(DateTime? value) => _filtersChanged = true;
    partial void OnIncludeSimulatedChanged(bool value) => _filtersChanged = true;
    partial void OnSelectedStateChanged(string value) => _filtersChanged = true;
    partial void OnSelectedFaultTypeChanged(string value) => _filtersChanged = true;
    partial void OnSelectedSeverityChanged(string value) => _filtersChanged = true;
    public void Cancel() => _cancellation?.Cancel();

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        _initialized = false;
        await Run(async ct =>
        {
            Models.Clear(); Models.Add(new(null, "全部型号"));
            for (var page = 1; ; page++)
            {
                var result = await registry.ListModelsAsync(new(Page: page), ct);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
                foreach (var model in result.Value!.Items) Models.Add(new(model.Id, $"{model.Manufacturer} / {model.ModelName}"));
                if ((long)page * result.Value.PageSize >= result.Value.TotalCount) break;
            }
            SelectedModel = Models[0];
            _initialized = true;
        });
        if (_initialized) await RefreshAsync();
    }
    public Task RefreshAsync() { if (IsBusy) return Task.CompletedTask; if (!_initialized) return InitializeAsync(); _page = 1; return LoadAsync(); }
    public Task PreviousAsync() { if (IsBusy) return Task.CompletedTask; if (_filtersChanged) return RefreshAsync(); if (_page == 1) return Task.CompletedTask; _page--; return LoadAsync(); }
    public Task NextAsync() { if (IsBusy) return Task.CompletedTask; if (_filtersChanged) return RefreshAsync(); if ((long)_page * 50 >= _total) return Task.CompletedTask; _page++; return LoadAsync(); }
    private Task LoadAsync() => Run(async ct =>
    {
        LifeRows.Clear(); Trend.Clear(); _total = 0; OnPropertyChanged(nameof(PageInfo)); DashboardText = "正在查询…"; TrendSummary = "正在查询…";
        if (AsOf is null || TrendFrom is null || TrendTo is null) throw new InvalidOperationException("请选择统计日期与趋势起止日期。");
        var date = DateOnly.FromDateTime(AsOf.Value);
        long? deviceId = null;
        if (!string.IsNullOrWhiteSpace(AssetCode))
        {
            var code = AssetCode.Trim();
            for (var page = 1; ; page++)
            {
                var found = await registry.ListDevicesAsync(new(Page: page, Search: code), ct);
                if (!found.IsSuccess) throw new InvalidOperationException(found.Error!.Message);
                var device = found.Value!.Items.FirstOrDefault(x => string.Equals(x.AssetCode, code, StringComparison.Ordinal));
                if (device is not null) { deviceId = device.Id; break; }
                if ((long)page * found.Value.PageSize >= found.Value.TotalCount) throw new InvalidOperationException("未找到该资产编号，请输入完整编号。");
            }
        }
        var life = await service.DeviceLifeAsync(new(date, _page, 50, SelectedModel?.Id, deviceId, string.IsNullOrEmpty(SelectedState) ? null : SelectedState, IncludeSimulated), ct);
        var trend = await service.FaultTrendAsync(new(DateOnly.FromDateTime(TrendFrom.Value), DateOnly.FromDateTime(TrendTo.Value), SelectedModel?.Id, deviceId, string.IsNullOrEmpty(SelectedFaultType) ? null : SelectedFaultType, string.IsNullOrEmpty(SelectedSeverity) ? null : SelectedSeverity, IncludeSimulated), ct);
        var dashboard = await service.DashboardAsync(new(date, IncludeSimulated), ct);
        if (!life.IsSuccess) throw new InvalidOperationException(life.Error!.Message);
        if (!trend.IsSuccess) throw new InvalidOperationException(trend.Error!.Message);
        if (!dashboard.IsSuccess) throw new InvalidOperationException(dashboard.Error!.Message);
        foreach (var row in life.Value!.Items) LifeRows.Add(new(row));
        _total = life.Value.TotalCount; OnPropertyChanged(nameof(PageInfo));
        var report = trend.Value!;
        var max = report.Points.Count == 0 ? 1 : report.Points.Max(x => x.Count);
        foreach (var point in report.Points) Trend.Add(new(point.Month, point.Count, max == 0 ? 0 : 280d * point.Count / max));
        TrendSummary = report.TotalFaults == 0 ? "所选范围没有故障记录。" : $"故障 {report.TotalFaults} 条；有效关闭 {report.ClosedFaultCount} 条；平均登记至关闭耗时：{(report.AverageDurationSeconds is { } seconds ? seconds.ToString("N2", CultureInfo.CurrentCulture) + " 秒" : "不可计算")}";
        var d = dashboard.Value!;
        DashboardText = $"全库概览：设备 {d.DeviceCount} 台 · 未关闭异常 {d.OpenAnomalyCount} 条 · 待处理故障 {d.OpenFaultCount} 条 · 耗材 {d.ConsumableCount} 种 · 零库存 {d.ZeroStockCount} 种";
        Feedback = "统计已刷新；概览仅按模拟数据选项筛选，明细和趋势同时应用型号与资产编号。";
        _filtersChanged = false;
    });
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Feedback = "查询已取消，请重新查询。"; DashboardText = "查询未完成"; TrendSummary = "查询未完成"; }
        catch (Exception ex) { logger.LogError(ex, "统计查询失败"); Feedback = ex.Message; DashboardText = "查询未完成"; TrendSummary = "查询未完成"; }
        finally { _cancellation = null; IsBusy = false; }
    }
}
