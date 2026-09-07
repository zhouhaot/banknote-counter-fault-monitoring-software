using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core;
using MoneyCounter.Core.Inventory;
using MoneyCounter.Core.Registry;
using MoneyCounter.Core.Maintenance;

namespace MoneyCounter.Desktop.ViewModels;

public sealed record InventoryMovementRow(MovementDetail Record)
{
    public string Name => Record.ConsumableName;
    public string Type => Record.ReversesId is not null ? "冲正" : InventoryViewModel.Types.FirstOrDefault(x => x.Code == Record.MovementType)?.Label ?? Record.MovementType;
    public string Quantity => Record.Quantity.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    public string Time => OperationsViewModel.DisplayTime(Record.OccurredAt);
    public string Device => Record.AssetCode ?? "—";
    public string Fault => Record.FaultId?.ToString() ?? "—";
    public string Reason => Record.Reason;
    public string Source => OperationsViewModel.SourceLabel(Record.Source);
    public string Reversal => Record.HasDirectReversal ? "已冲正" : Record.ReversesId is { } id ? $"冲正 #{id}" : "—";
}
public partial class InventoryViewModel : ObservableObject
{
    private readonly IInventoryService _service;
    private readonly IRegistryService _registry;
    private readonly IMaintenanceService _maintenance;
    private readonly ILogger<InventoryViewModel> _logger;
    private ConsumableDetail? _editing;
    private ConsumableDetail? _movementTarget;
    private bool _catalogDirty, _movementDirty, _loading;
    private int _page = 1, _movementPage = 1;
    private long _total, _movementTotal;
    private readonly Dictionary<string, (string Payload, Guid Id)> _operations = [];
    public InventoryViewModel(IInventoryService service, IRegistryService registry, IMaintenanceService maintenance, ILogger<InventoryViewModel> logger, DeviceDetail? device = null, FaultDetail? fault = null)
    {
        _service = service; _registry = registry; _maintenance = maintenance; _logger = logger;
        selectedDevice = device; selectedFault = fault;
        if (fault is not null) movementType = "ISSUE";
        if (device is not null) Devices.Add(device);
        if (fault is not null) Faults.Add(fault);
        ContextText = fault is null ? "库存流水独立保存；数量最多两位小数。" : $"维修关联：{device?.AssetCode ?? fault.AssetCode} / {fault.FaultNo}。库存操作独立保存，失败不会撤销已保存维修。";
    }
    public string ContextText { get; }
    public static IReadOnlyList<StateOption> Types { get; } = [new("INBOUND", "入库"), new("ISSUE", "领用（扣减）"), new("RETURN", "退回"), new("ADJUST", "调整（可正可负）")];
    public ObservableCollection<ConsumableDetail> Consumables { get; } = [];
    public ObservableCollection<InventoryMovementRow> Movements { get; } = [];
    public ObservableCollection<DeviceDetail> Devices { get; } = [];
    public ObservableCollection<FaultDetail> Faults { get; } = [];
    public Func<string, bool>? Confirm { get; set; }
    public bool HasUnsavedChanges => _catalogDirty || _movementDirty;
    [ObservableProperty] private bool isBusy;
    public bool IsReady => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));
    [ObservableProperty] private string feedback = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private string movementPageInfo = "";
    [ObservableProperty] private ConsumableDetail? selectedConsumable;
    [ObservableProperty] private InventoryMovementRow? selectedMovement;
    [ObservableProperty] private string consumableName = "";
    [ObservableProperty] private string unit = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string movementType = "INBOUND";
    [ObservableProperty] private string quantity = "";
    [ObservableProperty] private string occurredAt = OperationsViewModel.DisplayTime(DateTimeOffset.UtcNow);
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private DeviceDetail? selectedDevice;
    [ObservableProperty] private FaultDetail? selectedFault;
    [ObservableProperty] private string deviceSearch = "";
    partial void OnSelectedConsumableChanged(ConsumableDetail? oldValue, ConsumableDetail? newValue)
    {
        if (_loading || !_movementDirty || newValue is null) return;
        if (_movementTarget is null || _movementTarget.Id == newValue.Id) { _movementTarget = newValue; return; }
        if (Confirm?.Invoke("库存流水尚未保存，是否将当前输入用于新选中的耗材？") == true) { _movementTarget = newValue; return; }
        _loading = true;
        try { SelectedConsumable = Consumables.FirstOrDefault(x => x.Id == _movementTarget.Id); }
        finally { _loading = false; }
    }
    partial void OnConsumableNameChanged(string value) { if (!_loading) _catalogDirty = true; }
    partial void OnUnitChanged(string value) { if (!_loading) _catalogDirty = true; }
    partial void OnNotesChanged(string value) { if (!_loading) _catalogDirty = true; }
    private void MarkMovementDirty() { _movementTarget ??= SelectedConsumable; _movementDirty = true; }
    partial void OnMovementTypeChanged(string value) => MarkMovementDirty();
    partial void OnQuantityChanged(string value) => MarkMovementDirty();
    partial void OnOccurredAtChanged(string value) => MarkMovementDirty();
    partial void OnReasonChanged(string value) => MarkMovementDirty();
    partial void OnSelectedDeviceChanged(DeviceDetail? value) { if (_loading) return; SelectedFault = null; Faults.Clear(); MarkMovementDirty(); }
    partial void OnSelectedFaultChanged(FaultDetail? value) { if (!_loading) MarkMovementDirty(); }
    private Guid Operation(string kind, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        if (!_operations.TryGetValue(kind, out var old) || old.Payload != json) _operations[kind] = (json, Guid.NewGuid());
        return _operations[kind].Id;
    }
    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception ex) { _logger.LogError(ex, "库存操作失败"); Feedback = "操作失败，输入已保留。请重试或检查日志。"; }
        finally { IsBusy = false; }
    }
    private static T Value<T>(Result<T> r) => r.IsSuccess ? r.Value! : throw new InvalidOperationException(r.Error!.Message);
    private async Task Load()
    {
        var selected = SelectedConsumable?.Id;
        var list = Value(await _service.ListConsumablesAsync(new(_page, Search: Search)));
        _loading = true;
        try { Consumables.Clear(); foreach (var item in list.Items) Consumables.Add(item); SelectedConsumable = Consumables.FirstOrDefault(x => x.Id == selected); }
        finally { _loading = false; }
        _total = list.TotalCount; PageInfo = $"第 {_page} 页，共 {_total} 条";
        var movements = Value(await _service.ListMovementsAsync(new(Page: _movementPage)));
        Movements.Clear(); foreach (var item in movements.Items) Movements.Add(new(item));
        SelectedMovement = null; _movementTotal = movements.TotalCount; MovementPageInfo = $"第 {_movementPage} 页，共 {_movementTotal} 条";
    }
    public Task RefreshAsync() => Run(async () => { await Load(); Feedback = "已刷新库存与流水"; });
    [RelayCommand] private Task SearchCatalog() => Run(async () => { _page = 1; await Load(); });
    [RelayCommand] private Task Previous() => Run(async () => { if (_page > 1) { _page--; await Load(); } });
    [RelayCommand] private Task Next() => Run(async () => { if (_page * 50L < _total) { _page++; await Load(); } });
    [RelayCommand] private Task PreviousMovement() => Run(async () => { if (_movementPage > 1) { _movementPage--; await Load(); } });
    [RelayCommand] private Task NextMovement() => Run(async () => { if (_movementPage * 50L < _movementTotal) { _movementPage++; await Load(); } });
    private bool DiscardCatalog() => !_catalogDirty || Confirm?.Invoke("放弃未保存的耗材资料？") == true;
    private void ClearCatalog() { _loading = true; _editing = null; ConsumableName = Unit = Notes = ""; _loading = false; _catalogDirty = false; }
    [RelayCommand] private void NewConsumable() { if (!IsReady || !DiscardCatalog()) return; ClearCatalog(); Feedback = "新增耗材"; }
    [RelayCommand] private void EditConsumable()
    {
        if (!IsReady || SelectedConsumable is not { } item || !DiscardCatalog()) return;
        _loading = true; _editing = item; ConsumableName = item.Name; Unit = item.Unit; Notes = item.Notes; _loading = false; _catalogDirty = false; Feedback = $"编辑：{item.Name}";
    }
    private async Task Committed(string key, string message)
    {
        _operations.Remove(key); Feedback = message;
        try { await Load(); }
        catch (Exception ex) { _logger.LogError(ex, "库存已提交但刷新失败"); Feedback = message + "；刷新失败，请点击刷新查看，不要重复提交。"; }
    }
    [RelayCommand] private Task SaveConsumable() => Run(async () =>
    {
        var id = Operation("catalog", new { _editing?.Id, _editing?.Revision, ConsumableName, Unit, Notes });
        var result = _editing is { } edit ? await _service.UpdateConsumableAsync(new(id, edit.Id, edit.Revision, ConsumableName, Unit, Notes)) : await _service.CreateConsumableAsync(new(id, ConsumableName, Unit, Notes));
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        ClearCatalog(); await Committed("catalog", "耗材已保存");
    });
    [RelayCommand] private Task Deactivate() => Run(async () =>
    {
        if (SelectedConsumable is not { } item) { Feedback = "请先选择耗材"; return; }
        if (Confirm?.Invoke($"确认停用 {item.Name}？历史流水将保留。") != true) return;
        var r = await _service.DeactivateConsumableAsync(new(Operation("deactivate", new { item.Id, item.Revision }), item.Id, item.Revision));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        await Committed("deactivate", "耗材已停用");
    });
    [RelayCommand] private Task FindDevices() => Run(async () =>
    {
        var items = Value(await _registry.ListDevicesAsync(new(PageSize: 50, Search: DeviceSearch)));
        var selected = SelectedDevice; var fault = SelectedFault; _loading = true;
        try {
            Devices.Clear(); if (selected is not null && !items.Items.Any(x => x.Id == selected.Id)) Devices.Add(selected);
            foreach (var item in items.Items) Devices.Add(item);
            SelectedDevice = Devices.FirstOrDefault(x => x.Id == selected?.Id); SelectedFault = fault;
        } finally { _loading = false; }
        Feedback = items.TotalCount > 50 ? "仅显示前 50 台设备，请缩小搜索范围。" : $"找到 {items.TotalCount} 台设备";
    });
    [RelayCommand] private Task LoadFaults() => Run(async () =>
    {
        if (SelectedDevice is not { } device) { Feedback = "请先选择设备"; return; }
        var items = Value(await _maintenance.ListFaultsAsync(new(PageSize: 50, DeviceId: device.Id)));
        var selected = SelectedFault; _loading = true;
        try {
            Faults.Clear();
            if (selected is not null && !items.Items.Any(x => x.Id == selected.Id)) Faults.Add(selected);
            foreach (var item in items.Items) Faults.Add(item);
            SelectedFault = Faults.FirstOrDefault(x => x.Id == selected?.Id);
        } finally { _loading = false; }
        Feedback = items.TotalCount > 50 ? "显示此设备最近 50 条故障；其他故障请从维修详情打开库存窗口。" : "已加载设备故障，可选关联";
    });
    [RelayCommand] private void ClearAssociation() { if (!IsReady) return; SelectedDevice = null; SelectedFault = null; }
    private bool ParseTime(out DateTimeOffset time)
    {
        time = default;
        if (!DateTime.TryParseExact(OccurredAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) { Feedback = "请输入 yyyy-MM-dd HH:mm:ss 格式的北京时间（UTC+8）"; return false; }
        time = new(parsed, TimeSpan.FromHours(8)); return true;
    }
    [RelayCommand] private Task PostMovement() => Run(async () =>
    {
        if (SelectedConsumable is not { } item) { Feedback = "请先在耗材表中选择耗材"; return; }
        if (!decimal.TryParse(Quantity, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity) || decimal.Round(quantity, 2) != quantity) { Feedback = "数量必须是最多两位小数的数字"; return; }
        if (!ParseTime(out var time)) return;
        if (MovementType == "ISSUE" && SelectedDevice is null) { Feedback = "领用必须选择设备"; return; }
        if (SelectedFault is { } fault && fault.DeviceId != SelectedDevice?.Id) { Feedback = "故障必须属于所选设备"; return; }
        var op = Operation("movement", new { item.Id, MovementType, quantity, time, Reason, DeviceId = SelectedDevice?.Id, FaultId = SelectedFault?.Id });
        var r = await _service.PostMovementAsync(new(op, item.Id, MovementType, quantity, time, Reason, SelectedDevice?.Id, SelectedFault?.Id));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        Quantity = Reason = ""; _movementDirty = false; _movementTarget = null; _movementPage = 1; await Committed("movement", "库存流水已保存");
    });
    [RelayCommand] private Task ReverseMovement() => Run(async () =>
    {
        if (SelectedMovement is not { } row) { Feedback = "请先选择流水"; return; }
        if (!ParseTime(out var time)) return;
        if (string.IsNullOrWhiteSpace(Reason)) { Feedback = "请输入冲正原因"; return; }
        if (Confirm?.Invoke($"确认对流水 #{row.Record.Id} 冲正？将新增反向流水，原记录保留。") != true) return;
        var r = await _service.ReverseMovementAsync(new(Operation("reverse", new { row.Record.Id, time, Reason }), row.Record.Id, time, Reason));
        if (!r.IsSuccess) { Feedback = r.Error!.Message; return; }
        Reason = ""; _movementDirty = Quantity.Length > 0; if (!_movementDirty) _movementTarget = null; await Committed("reverse", "冲正已保存");
    });
}
