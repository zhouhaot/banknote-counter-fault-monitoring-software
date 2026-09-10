using System.Windows;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Backup;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;
public partial class BackupWindow : Window
{
    private readonly BackupViewModel _vm;
    public BackupWindow(IBackupService service, ILogger<BackupViewModel> logger)
    {
        InitializeComponent(); _vm = new(service, logger); DataContext = _vm;
        _vm.Confirm = text => MessageBox.Show(this, text, "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await _vm.RefreshAsync();
        Closing += (_, e) => { if (_vm.IsBusy) { e.Cancel = true; _vm.Feedback = "操作进行中，请取消并等待完成后关闭。"; } };
    }
    private async void CreateClick(object sender, RoutedEventArgs e) => await _vm.CreateAsync();
    private async void RestoreClick(object sender, RoutedEventArgs e) => await _vm.RestoreAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
