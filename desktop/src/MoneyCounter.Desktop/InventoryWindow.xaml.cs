using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Inventory;
using MoneyCounter.Core.Registry;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;

public partial class InventoryWindow : Window
{
    private readonly InventoryViewModel _viewModel;
    public InventoryWindow(IInventoryService service, IRegistryService registry, IMaintenanceService maintenance,
        ILogger<InventoryViewModel> logger, DeviceDetail? device = null, FaultDetail? fault = null)
    {
        _viewModel = new(service, registry, maintenance, logger, device, fault);
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Confirm = message => MessageBox.Show(this, message, "耗材库存", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
        Closing += (_, e) =>
        {
            if (_viewModel.IsBusy) { e.Cancel = true; _viewModel.Feedback = "操作进行中，请等待完成。"; }
            else if (_viewModel.HasUnsavedChanges && !_viewModel.Confirm("尚有未保存内容，是否放弃并关闭？")) e.Cancel = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();
}
