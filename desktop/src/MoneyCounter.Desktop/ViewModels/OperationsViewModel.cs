using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core;
using MoneyCounter.Core.Operations;
using MoneyCounter.Core.Registry;

namespace MoneyCounter.Desktop.ViewModels;

public sealed record StateOption(string Code, string Label);
public sealed record StatusRow(StatusDetail Record)
{
    public string Time => OperationsViewModel.DisplayTime(Record.RecordedAt);
    public string State => OperationsViewModel.Label(Record.Status);
    public long Count => Record.CumulativeCount;
    public string Increment => Record.Increment?.ToString() ?? "首条记录";
    public string Notes => Record.Notes;
    public string Source => OperationsViewModel.SourceLabel(Record.Source);
}
public sealed record AnomalyRow(AnomalyDetail Record)
{
    public string Time => OperationsViewModel.DisplayTime(Record.DiscoveredAt);
    public string State => Record.Status switch { "OPEN" => "待处理", "CLOSED" => "已关闭", _ => "已转故障" };
    public string Description => Record.Description;
    public string Handling => Record.HandlingNotes;
    public string Closed => Record.ClosedAt is { } t ? OperationsViewModel.DisplayTime(t) : "";
    public string Source => OperationsViewModel.SourceLabel(Record.Source);
}

public partial class OperationsViewModel(DeviceDetail device, IOperationsService service, ILogger<OperationsViewModel> logger) : ObservableObject
{
    public string Title => $"{device.AssetCode} · 状态与异常";
    public static IReadOnlyList<StateOption> States { get; } = [new("RUNNING", "运行"), new("STOPPED", "停机"), new("FAULT", "故障"), new("MAINTENANCE", "维护"), new("RETIRED", "报废")];
    public static string Label(string code) => States.FirstOrDefault(s => s.Code == code)?.Label ?? code;
    public static string SourceLabel(string source) => source switch { "MANUAL" => "人工录入", "CSV" => "CSV导入", "SIMULATED" => "模拟数据", _ => source };
    public static string DisplayTime(DateTimeOffset value) => value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Now => DisplayTime(DateTimeOffset.UtcNow);
    public ObservableCollection<StatusRow> Statuses { get; } = [];
    public ObservableCollection<AnomalyRow> Anomalies { get; } = [];
    [ObservableProperty] private bool isBusy;
    public bool IsReady => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));
    [ObservableProperty] private string feedback = "";
    [ObservableProperty] private string latest = "尚无状态记录";
    [ObservableProperty] private string recordedAt = Now;
    [ObservableProperty] private string cumulativeCount = "";
    [ObservableProperty] private string selectedState = "RUNNING";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string discoveredAt = Now;
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string closedAt = Now;
    [ObservableProperty] private string handlingNotes = "";
    [ObservableProperty] private AnomalyRow? selectedAnomaly;
    [ObservableProperty] private string statusPageInfo = "";
    [ObservableProperty] private string anomalyPageInfo = "";
    private int _statusPage = 1, _anomalyPage = 1;
    private long _statusTotal, _anomalyTotal;
    private bool _statusDirty, _anomalyDirty, _closingDirty;
    public bool HasUnsavedChanges => _statusDirty || _anomalyDirty || _closingDirty;
    partial void OnRecordedAtChanged(string value) => _statusDirty = true;
    partial void OnCumulativeCountChanged(string value) => _statusDirty = true;
    partial void OnSelectedStateChanged(string value) => _statusDirty = true;
    partial void OnNotesChanged(string value) => _statusDirty = true;
    partial void OnDiscoveredAtChanged(string value) => _anomalyDirty = true;
    partial void OnDescriptionChanged(string value) => _anomalyDirty = true;
    partial void OnClosedAtChanged(string value) => _closingDirty = true;
    partial void OnHandlingNotesChanged(string value) => _closingDirty = true;
    private readonly Dictionary<string, (string Payload, Guid Id)> _operations = [];
    private Guid Operation(string kind, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        if (!_operations.TryGetValue(kind, out var previous) || previous.Payload != json) _operations[kind] = (json, Guid.NewGuid());
        return _operations[kind].Id;
    }
    private static bool ParseTime(string input, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParseExact(input, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        value = new DateTimeOffset(parsed, TimeSpan.FromHours(8));
        return true;
    }
    public async Task RefreshAsync() => await Run(async () => { await LoadAsync(); Feedback = "历史记录已刷新"; });
    private async Task LoadAsync()
    {
        var statuses = await service.ListStatusesAsync(new(Page: _statusPage, PageSize: 50, DeviceId: device.Id));
        var anomalies = await service.ListAnomaliesAsync(new(Page: _anomalyPage, PageSize: 50, DeviceId: device.Id));
        var recent = await service.LatestStatusAsync(device.Id);
        if (!statuses.IsSuccess || !anomalies.IsSuccess || !recent.IsSuccess) throw new InvalidOperationException(statuses.Error?.Message ?? anomalies.Error?.Message ?? recent.Error?.Message);
        Statuses.Clear(); foreach (var row in statuses.Value!.Items) Statuses.Add(new(row));
        Anomalies.Clear(); foreach (var row in anomalies.Value!.Items) Anomalies.Add(new(row));
        _statusTotal = statuses.Value.TotalCount; _anomalyTotal = anomalies.Value.TotalCount;
        StatusPageInfo = $"第 {_statusPage} 页，共 {_statusTotal} 条";
        AnomalyPageInfo = $"第 {_anomalyPage} 页，共 {_anomalyTotal} 条";
        Latest = recent.Value is { } r ? $"最近业务记录：{Label(r.Status)} · {DisplayTime(r.RecordedAt)}（北京时间）· {SourceLabel(r.Source)}" : "尚无状态记录";
    }
    [RelayCommand] private async Task SaveStatus() => await Run(async () =>
    {
        if (!ParseTime(RecordedAt, out var time)) { Feedback = "记录时间格式须为 yyyy-MM-dd HH:mm:ss（北京时间）"; return; }
        if (!long.TryParse(CumulativeCount, out var count) || count < 0) { Feedback = "累计读数须为 0 至 9223372036854775807 的整数"; return; }
        var payload = new { time, SelectedState, count, Notes };
        var result = await service.RecordStatusAsync(new(Operation("status", payload), device.Id, time, SelectedState, count, Notes));
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        _operations.Remove("status"); CumulativeCount = ""; Notes = ""; _statusDirty = false; _statusPage = 1; await RefreshAfterCommit("状态记录已保存");
    });
    [RelayCommand] private async Task SaveAnomaly() => await Run(async () =>
    {
        if (!ParseTime(DiscoveredAt, out var time)) { Feedback = "发现时间格式须为 yyyy-MM-dd HH:mm:ss（北京时间）"; return; }
        var result = await service.CreateAnomalyAsync(new(Operation("anomaly", new { time, Description }), device.Id, time, Description));
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        _operations.Remove("anomaly"); Description = ""; _anomalyDirty = false; _anomalyPage = 1; await RefreshAfterCommit("异常已登记");
    });
    [RelayCommand] private async Task CloseAnomaly() => await Run(async () =>
    {
        if (SelectedAnomaly is not { } selected) { Feedback = "请先选择一条待处理异常"; return; }
        if (!ParseTime(ClosedAt, out var time)) { Feedback = "关闭时间格式须为 yyyy-MM-dd HH:mm:ss（北京时间）"; return; }
        var row = selected.Record;
        var result = await service.CloseAnomalyAsync(new(Operation("close", new { row.Id, row.Revision, time, HandlingNotes }), row.Id, row.Revision, time, HandlingNotes));
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        _operations.Remove("close"); HandlingNotes = ""; _closingDirty = false; await RefreshAfterCommit("异常已关闭，历史记录已保留");
    });
    [RelayCommand] private async Task PreviousStatus() { if (_statusPage > 1) await Run(async () => { _statusPage--; await LoadAsync(); }); }
    [RelayCommand] private async Task NextStatus() { if ((long)_statusPage * 50 < _statusTotal) await Run(async () => { _statusPage++; await LoadAsync(); }); }
    [RelayCommand] private async Task PreviousAnomaly() { if (_anomalyPage > 1) await Run(async () => { _anomalyPage--; await LoadAsync(); }); }
    [RelayCommand] private async Task NextAnomaly() { if ((long)_anomalyPage * 50 < _anomalyTotal) await Run(async () => { _anomalyPage++; await LoadAsync(); }); }
    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Feedback = "操作进行中…";
        try { await action(); }
        catch (Exception ex) { logger.LogError(ex, "Operations action failed"); Feedback = "操作失败，输入已保留。请检查日志后重试。"; }
        finally { IsBusy = false; }
    }
    private async Task RefreshAfterCommit(string success)
    {
        try { await LoadAsync(); Feedback = success; }
        catch (Exception ex) { logger.LogError(ex, "History refresh failed after commit"); Feedback = success + "；历史刷新失败，请重新打开此窗口查看，勿重复提交。"; }
    }
}
