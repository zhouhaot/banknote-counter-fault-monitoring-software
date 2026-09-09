using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Simulation;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;
public partial class SimulationWindow : Window
{
    private readonly SimulationViewModel _vm;
    public SimulationWindow(ISimulationService service, ILogger<SimulationViewModel> logger)
    {
        InitializeComponent(); _vm = new(service, logger); DataContext = _vm;
        _vm.Confirm = text => MessageBox.Show(this, text, "清除模拟集", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await _vm.RefreshAsync();
        Closing += (_, e) => { if (_vm.IsBusy) { e.Cancel = true; _vm.Feedback = "操作进行中，请取消并等待事务结束后关闭。"; } };
    }
    private async void CreateClick(object sender, RoutedEventArgs e) => await _vm.CreateAsync();
    private async void ResetClick(object sender, RoutedEventArgs e) => await _vm.ResetAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
