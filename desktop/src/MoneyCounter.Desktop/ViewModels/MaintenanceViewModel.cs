using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Core.Operations;
using MoneyCounter.Core.Registry;

namespace MoneyCounter.Desktop.ViewModels;

public sealed record FaultRow(FaultDetail Record)
{
    public string Number => Record.FaultNo;
    public string Time => OperationsViewModel.DisplayTime(Record.RegisteredAt);
    public string Type => MaintenanceViewModel.Types.FirstOrDefault(x => x.Code == Record.FaultType)?.Label ?? Record.FaultType;
    public string Severity => MaintenanceViewModel.Severities.FirstOrDefault(x => x.Code == Record.Severity)?.Label ?? Record.Severity;
    public string Status => Record.Status switch { "PENDING" => "待处理", "IN_PROGRESS" => "处理中", "CLOSED" => "已关闭", _ => Record.Status };
    public string Description => Record.Description;
    public string Source => OperationsViewModel.SourceLabel(Record.Source);
}
public sealed record RepairRow(RepairDetail Record)
{
    public string Time => OperationsViewModel.DisplayTime(Record.RepairedAt);
    public string Action => Record.Action;
    public string Result => Record.Result;
    public string Technician => Record.Technician;
    public string Notes => Record.Notes;
}
public partial class MaintenanceViewModel : ObservableObject
{
    private readonly DeviceDetail _device;
    private readonly IMaintenanceService _service;
    private readonly ILogger<MaintenanceViewModel> _logger;
    private AnomalyDetail? _source;
    public MaintenanceViewModel(DeviceDetail device, IMaintenanceService service, ILogger<MaintenanceViewModel> logger, AnomalyDetail? sourceAnomaly = null)
    {
        _device = device; _service = service; _logger = logger; _source = sourceAnomaly;
        if (_source is { } a) { registeredAt = OperationsViewModel.DisplayTime(a.DiscoveredAt); description = a.Description; }
    }
    public string Title => $"{_device.AssetCode} · 故障与维修";
    public string CreationSource => _source is { } a ? $"将异常 #{a.Id} 转为故障" : "独立登记故障";
    public static IReadOnlyList<StateOption> Types { get; } = [new("CARD_JAM", "卡钞"), new("COUNTING", "计数"), new("AUTHENTICATION", "鉴伪"), new("FEED", "进钞"), new("DISPLAY", "显示"), new("POWER", "电源"), new("MECHANICAL", "机械"), new("OTHER", "其他")];
    public static IReadOnlyList<StateOption> Severities { get; } = [new("LOW", "轻微"), new("MEDIUM", "一般"), new("HIGH", "严重"), new("URGENT", "紧急")];
    public ObservableCollection<FaultRow> Faults { get; } = [];
    public ObservableCollection<RepairRow> Repairs { get; } = [];
    public Func<string, bool>? Confirm { get; set; }
    [ObservableProperty] private bool isBusy;
    public bool IsReady => !IsBusy;
    public bool CanMaintain => !IsBusy && CurrentFault?.Status == "IN_PROGRESS";
    public bool CanStart => !IsBusy && CurrentFault?.Status == "PENDING";
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(CanMaintain)); OnPropertyChanged(nameof(CanStart)); }
    [ObservableProperty] private string feedback = "";
    [ObservableProperty] private string registeredAt = Now;
    [ObservableProperty] private string faultType = "OTHER";
    [ObservableProperty] private string severity = "MEDIUM";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string actionAt = Now;
    [ObservableProperty] private string finalResult = "";
    [ObservableProperty] private string repairedAt = Now;
    [ObservableProperty] private string repairAction = "";
    [ObservableProperty] private string repairResult = "";
    [ObservableProperty] private string technician = "";
    [ObservableProperty] private string repairNotes = "";
    [ObservableProperty] private FaultRow? selectedFault;
    [ObservableProperty] private RepairRow? selectedRepair;
    [ObservableProperty] private FaultDetail? currentFault;
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private string repairPageInfo = "";
    public string DetailText => CurrentFault is not { } f ? "请选择故障并打开详情" : $"{f.FaultNo} · {new FaultRow(f).Status} · 来源异常：{f.SourceAnomalyId?.ToString() ?? "无"}\n登记：{OperationsViewModel.DisplayTime(f.RegisteredAt)}\n{f.Description}\n开始：{(f.StartedAt is { } s ? OperationsViewModel.DisplayTime(s) : "—")}　关闭：{(f.ClosedAt is { } c ? OperationsViewModel.DisplayTime(c) : "—")}\n最终结论：{f.FinalResult}";
    partial void OnCurrentFaultChanged(FaultDetail? value) { OnPropertyChanged(nameof(DetailText)); OnPropertyChanged(nameof(CanMaintain)); OnPropertyChanged(nameof(CanStart)); }
    private static string Now => OperationsViewModel.DisplayTime(DateTimeOffset.UtcNow);
    private bool _createDirty, _repairDirty, _closeDirty;
    public bool HasUnsavedChanges => _createDirty || _repairDirty || _closeDirty;
    partial void OnRegisteredAtChanged(string value) => _createDirty = true;
    partial void OnFaultTypeChanged(string value) => _createDirty = true;
    partial void OnSeverityChanged(string value) => _createDirty = true;
    partial void OnDescriptionChanged(string value) => _createDirty = true;
    partial void OnActionAtChanged(string value) => _closeDirty = true;
    partial void OnFinalResultChanged(string value) => _closeDirty = true;
    partial void OnRepairedAtChanged(string value) => _repairDirty = true;
    partial void OnRepairActionChanged(string value) => _repairDirty = true;
    partial void OnRepairResultChanged(string value) => _repairDirty = true;
    partial void OnTechnicianChanged(string value) => _repairDirty = true;
    partial void OnRepairNotesChanged(string value) => _repairDirty = true;
    private int _page = 1, _repairPage = 1;
    private long _total, _repairTotal;
    private long? _editingRepair;
    private readonly Dictionary<string, (string Payload, Guid Id)> _operations = [];
    private Guid Operation(string kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (!_operations.TryGetValue(kind, out var entry) || entry.Payload != json) _operations[kind] = (json, Guid.NewGuid());
        return _operations[kind].Id;
    }
    private bool Time(string text, out DateTimeOffset result)
    {
        result = default;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { Feedback = "时间格式须为 yyyy-MM-dd HH:mm:ss（北京时间）"; return false; }
        result = new(parsed, TimeSpan.FromHours(8)); return true;
    }
    public Task RefreshAsync() => Run(async () => { await LoadFaults(); if (CurrentFault is not null) await LoadDetail(CurrentFault.Id); Feedback = "已刷新"; });
    private async Task LoadFaults()
    {
        var r = await _service.ListFaultsAsync(new(Page: _page, DeviceId: _device.Id));
        if (!r.IsSuccess) throw new InvalidOperationException(r.Error!.Message);
        Faults.Clear(); foreach (var f in r.Value!.Items) Faults.Add(new(f));
        _total = r.Value.TotalCount; PageInfo = $"第 {_page} 页，共 {_total} 条";
    }
    private async Task LoadDetail(long id)
    {
        var fault = await _service.GetFaultAsync(id);
        var repairs = await _service.ListRepairsAsync(id, _repairPage);
        if (!fault.IsSuccess || !repairs.IsSuccess) throw new InvalidOperationException(fault.Error?.Message ?? repairs.Error!.Message);
        CurrentFault = fault.Value;
        Repairs.Clear(); foreach (var r in repairs.Value!.Items) Repairs.Add(new(r));
        _repairTotal = repairs.Value.TotalCount; RepairPageInfo = $"维修第 {_repairPage} 页，共 {_repairTotal} 条";
    }
    [RelayCommand] private Task OpenFault() => Run(async () =>
    {
        if (SelectedFault is not { } row) { Feedback = "请先选择故障"; return; }
        if ((_repairDirty || _closeDirty) && Confirm?.Invoke("详情中尚有未保存内容，是否放弃并切换故障？") != true) return;
        _repairPage = 1; await LoadDetail(row.Record.Id); ClearRepair(); FinalResult = ""; ActionAt = Now; _closeDirty = false;
    });
    [RelayCommand] private Task CreateFault() => Run(async () =>
    {
        if (!Time(RegisteredAt, out var time)) return;
        if ((_repairDirty || _closeDirty) && Confirm?.Invoke("登记成功后将打开新故障，是否放弃当前详情中未保存的维修或关闭内容？") != true) return;
        var op = Operation("create", new { time, FaultType, Severity, Description, SourceId = _source?.Id });
        var r = _source is { } a ? await _service.ConvertAnomalyAsync(new(op, a.Id, a.Revision, time, FaultType, Severity, Description)) : await _service.CreateFaultAsync(new(op, _device.Id, time, FaultType, Severity, Description));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        _source = null; OnPropertyChanged(nameof(CreationSource)); Description = ""; _createDirty = false; _page = 1;
        ClearRepair(); FinalResult = ""; ActionAt = Now; _closeDirty = false; _repairPage = 1;
        await Committed("create", r.Value!, "故障已登记");
    });
    [RelayCommand] private Task StartFault() => Run(async () =>
    {
        if (CurrentFault is not { Status: "PENDING" } f || !Time(ActionAt, out var time)) return;
        var r = await _service.StartFaultAsync(new(Operation("start", new { f.Id, f.Revision, time }), f.Id, f.Revision, time));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        _closeDirty = FinalResult.Length > 0; await Committed("start", r.Value!, "已开始处理");
    });
    [RelayCommand] private Task CloseFault() => Run(async () =>
    {
        if (CurrentFault is not { Status: "IN_PROGRESS" } f || !Time(ActionAt, out var time)) return;
        if (_repairDirty) { Feedback = "请先保存或重置维修输入，再关闭故障"; return; }
        if (Confirm?.Invoke("关闭后故障和维修记录将只读，确认关闭？") != true) return;
        var r = await _service.CloseFaultAsync(new(Operation("close", new { f.Id, f.Revision, time, FinalResult }), f.Id, f.Revision, time, FinalResult));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        FinalResult = ""; _closeDirty = false; await Committed("close", r.Value!, "故障已关闭");
    });
    [RelayCommand] private void EditRepair()
    {
        if (!CanMaintain || SelectedRepair is not { } row) return;
        if (_repairDirty && Confirm?.Invoke("放弃未保存维修输入并编辑所选记录？") != true) return;
        var r = row.Record; _editingRepair = r.Id; RepairedAt = OperationsViewModel.DisplayTime(r.RepairedAt); RepairAction = r.Action; RepairResult = r.Result; Technician = r.Technician; RepairNotes = r.Notes; _repairDirty = false; Feedback = $"正在编辑维修 #{r.Id}";
    }
    [RelayCommand] private void NewRepair()
    {
        if (!IsReady) return;
        if (_repairDirty && Confirm?.Invoke("放弃未保存维修输入？") != true) return;
        ClearRepair(); Feedback = "可新增维修记录";
    }
    private void ClearRepair() { _editingRepair = null; SelectedRepair = null; RepairAction = ""; RepairResult = ""; Technician = ""; RepairNotes = ""; RepairedAt = Now; _repairDirty = false; }
    [RelayCommand] private Task SaveRepair() => Run(async () =>
    {
        if (CurrentFault is not { Status: "IN_PROGRESS" } f || !Time(RepairedAt, out var time)) return;
        var op = Operation("repair", new { f.Id, f.Revision, _editingRepair, time, RepairAction, RepairResult, Technician, RepairNotes });
        var r = _editingRepair is { } id ? await _service.UpdateRepairAsync(new(op, id, f.Revision, time, RepairAction, RepairResult, Technician, RepairNotes)) : await _service.AddRepairAsync(new(op, f.Id, f.Revision, time, RepairAction, RepairResult, Technician, RepairNotes));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        ClearRepair(); _repairPage = 1; await Committed("repair", r.Value!, "维修记录已保存");
    });
    [RelayCommand] private Task DeleteRepair() => Run(async () =>
    {
        if (CurrentFault is not { Status: "IN_PROGRESS" } f || SelectedRepair is not { } row) return;
        if (_repairDirty) { Feedback = "请先保存或重置维修输入"; return; }
        if (Confirm?.Invoke("确认删除所选维修记录？") != true) return;
        var r = await _service.DeleteRepairAsync(new(Operation("delete", new { row.Record.Id, f.Revision }), row.Record.Id, f.Revision));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        ClearRepair(); _repairPage = 1; await Committed("delete", r.Value!, "维修记录已删除");
    });
    [RelayCommand] private Task PreviousPage() => Run(async () => { if (_page > 1) { _page--; await LoadFaults(); } });
    [RelayCommand] private Task NextPage() => Run(async () => { if ((long)_page * 50 < _total) { _page++; await LoadFaults(); } });
    [RelayCommand] private Task PreviousRepairPage() => Run(async () => { if (_repairPage > 1 && CurrentFault is { } f) { _repairPage--; await LoadDetail(f.Id); } });
    [RelayCommand] private Task NextRepairPage() => Run(async () => { if ((long)_repairPage * 50 < _repairTotal && CurrentFault is { } f) { _repairPage++; await LoadDetail(f.Id); } });
    private async Task Committed(string kind, FaultDetail fault, string message)
    {
        _operations.Remove(kind); CurrentFault = fault;
        try { await LoadFaults(); await LoadDetail(fault.Id); Feedback = message; }
        catch (Exception ex) { _logger.LogError(ex, "Maintenance refresh failed after commit"); Feedback = message + "；刷新失败，请点击刷新，勿重复提交。"; }
    }
    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return; IsBusy = true; Feedback = "操作进行中…";
        try { await action(); }
        catch (Exception ex) { _logger.LogError(ex, "Maintenance action failed"); Feedback = "操作失败，输入已保留，请检查日志并重试。"; }
        finally { IsBusy = false; }
    }
}
