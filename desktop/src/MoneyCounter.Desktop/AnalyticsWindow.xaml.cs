using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Analytics;
using MoneyCounter.Core.Registry;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;
public partial class AnalyticsWindow : Window
{
    private readonly AnalyticsViewModel _vm;
    public AnalyticsWindow(IAnalyticsService service, IRegistryService registry, ILogger<AnalyticsViewModel> logger)
    {
        InitializeComponent(); _vm = new(service, registry, logger); DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closing += (_, e) => { if (_vm.IsBusy) { _vm.Cancel(); e.Cancel = true; } };
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
    private async void PreviousClick(object sender, RoutedEventArgs e) => await _vm.PreviousAsync();
    private async void NextClick(object sender, RoutedEventArgs e) => await _vm.NextAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
