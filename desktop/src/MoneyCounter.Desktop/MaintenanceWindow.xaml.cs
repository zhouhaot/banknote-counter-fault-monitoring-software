using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Core.Operations;
using MoneyCounter.Core.Registry;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;

public partial class MaintenanceWindow : Window
{
    private readonly MaintenanceViewModel _viewModel;
    public MaintenanceWindow(DeviceDetail device, IMaintenanceService service, ILogger<MaintenanceViewModel> logger, AnomalyDetail? sourceAnomaly = null)
    {
        InitializeComponent();
        _viewModel = new(device, service, logger, sourceAnomaly);
        _viewModel.Confirm = text => MessageBox.Show(this, text, "故障与维修", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
        Closing += (_, e) =>
        {
            if (_viewModel.IsBusy) { e.Cancel = true; _viewModel.Feedback = "操作进行中，请等待完成。"; }
            else if (_viewModel.HasUnsavedChanges && !_viewModel.Confirm("尚有未保存内容，是否放弃并关闭？")) e.Cancel = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();
    private async void OpenFault_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.OpenFaultCommand.ExecuteAsync(null);
        if (_viewModel.CurrentFault is not null) DetailTab.IsSelected = true;
    }
}
