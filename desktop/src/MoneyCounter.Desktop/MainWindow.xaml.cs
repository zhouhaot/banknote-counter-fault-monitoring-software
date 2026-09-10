using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Desktop.Infrastructure;
using MoneyCounter.Desktop.ViewModels;
namespace MoneyCounter.Desktop;
public partial class MainWindow : Window
{
    private readonly RegistryViewModel _vm;
    private readonly ILogger<RegistryEditor> _editorLogger;
    private readonly WindowPlacement _placement;
    private readonly Func<MoneyCounter.Core.Registry.DeviceDetail, OperationsViewModel> _operations;
    private readonly Func<MoneyCounter.Core.Registry.DeviceDetail, MoneyCounter.Core.Operations.AnomalyDetail?, MaintenanceWindow> _maintenance;
    private readonly Func<MoneyCounter.Core.Registry.DeviceDetail?, MoneyCounter.Core.Maintenance.FaultDetail?, Window> _inventory;
    private readonly Func<Window> _imports, _simulation, _analytics, _backup;
    public MainWindow(RegistryViewModel vm, ILogger<RegistryEditor> editorLogger, string windowPlacementPath, Func<MoneyCounter.Core.Registry.DeviceDetail, OperationsViewModel> operations, Func<MoneyCounter.Core.Registry.DeviceDetail, MoneyCounter.Core.Operations.AnomalyDetail?, MaintenanceWindow> maintenance, Func<MoneyCounter.Core.Registry.DeviceDetail?, MoneyCounter.Core.Maintenance.FaultDetail?, Window> inventory, Func<Window> imports, Func<Window> simulation, Func<Window> analytics, Func<Window> backup)
    {
        InitializeComponent(); _vm = vm; _editorLogger = editorLogger; _operations = operations; _maintenance = maintenance; _inventory = inventory; _imports = imports; _simulation = simulation; _analytics = analytics; _backup = backup; _placement = new WindowPlacement(windowPlacementPath); DataContext = vm;
        _placement.Restore(this);
        Loaded += (_, _) => _placement.EnsureVisible(this);
        Closing += (_, e) => { if (_vm.IsBusy) { e.Cancel = true; _vm.Status = "正在保存或查询，请等待完成后关闭。"; } };
        Closing += (_, e) => { if (!e.Cancel) _placement.Save(this); };
    }
    private async void DevicesClick(object sender, RoutedEventArgs e) => await Navigate(0);
    private void OperationsClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (_vm.Section != 0 || _vm.SelectedDevice is not { } device) { _vm.Status = "请在设备台账中选择一台设备，再打开状态与异常。"; return; }
        new OperationsWindow(_operations(device), a => _maintenance(device, a)) { Owner = this }.ShowDialog();
    }
    private void MaintenanceClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (_vm.Section != 0 || _vm.SelectedDevice is not { } device) { _vm.Status = "请在设备台账中选择一台设备，再打开故障与维修。"; return; }
        var window = _maintenance(device, null); window.Owner = this; window.ShowDialog();
    }
    private void InventoryClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        var window = _inventory(null, null); window.Owner = this; window.ShowDialog();
    }
    private async void AnalyticsClick(object sender, RoutedEventArgs e) => await OpenDataWindow(_analytics);
    private async void BackupClick(object sender, RoutedEventArgs e) => await OpenDataWindow(_backup);
    private async void ModelsClick(object sender, RoutedEventArgs e) => await Navigate(1);
    private async void ImportsClick(object sender, RoutedEventArgs e) => await OpenDataWindow(_imports);
    private async void SimulationClick(object sender, RoutedEventArgs e) => await OpenDataWindow(_simulation);
    private async Task OpenDataWindow(Func<Window> factory)
    {
        if (_vm.IsBusy) return;
        var window = factory(); window.Owner = this; window.ShowDialog();
        await _vm.RefreshAsync();
    }
    private async Task Navigate(int section)
    {
        if (_vm.IsBusy) return;
        _vm.Section = section; DeviceGrid.Visibility = section == 0 ? Visibility.Visible : Visibility.Collapsed; ModelGrid.Visibility = section == 1 ? Visibility.Visible : Visibility.Collapsed;
        await _vm.RefreshAsync();
    }
    private async void SearchClick(object sender, RoutedEventArgs e) { _vm.ResetPage(); await _vm.RefreshAsync(); }
    private async void ResetClick(object sender, RoutedEventArgs e) { _vm.Search = ""; _vm.ResetPage(); await _vm.RefreshAsync(); }
    private async void AddClick(object sender, RoutedEventArgs e) => await Edit(false);
    private async void EditClick(object sender, RoutedEventArgs e) => await Edit(true);
    private async Task Edit(bool existing)
    {
        if (_vm.IsBusy) return;
        if (existing && (_vm.Section == 0 ? _vm.SelectedDevice is null : _vm.SelectedModel is null)) { _vm.Status = "请先选择一条记录"; return; }
        var dialog = new RegistryEditor(_vm.Service, _editorLogger, _vm.Section, existing ? _vm.SelectedModel : null, existing ? _vm.SelectedDevice : null) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        await _vm.RefreshAsync();
        if (saved) _vm.Status = "保存成功";
    }
    private async void DeactivateClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (MessageBox.Show(this, "停用后不再用于新业务，历史记录仍保留。是否继续？", "停用记录", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await _vm.DeactivateAsync();
    }
    private void DeviceDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.SelectedDevice is not { } d) return;
        MessageBox.Show(this, $"资产编号：{d.AssetCode}\n型号：{d.Manufacturer} {d.ModelName}\n位置：{d.Location}\n负责人：{d.ResponsiblePerson}\n备注：{d.Notes}\n\n可通过左侧的状态与异常、故障与维修查看业务历史。", "设备详情");
    }
}
