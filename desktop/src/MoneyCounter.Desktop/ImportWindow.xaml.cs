using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MoneyCounter.Core.Imports;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;

public partial class ImportWindow : Window
{
    private readonly ImportViewModel _vm;
    public ImportWindow(ICsvImportService service, ILogger<ImportViewModel> logger)
    {
        InitializeComponent(); _vm = new(service, logger); DataContext = _vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();
        Closing += (_, e) => { if (_vm.IsBusy) { e.Cancel = true; _vm.Feedback = "操作进行中，请取消并等待回滚完成后关闭。"; } };
    }
    private void ChooseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _vm.FilePath = dialog.FileName;
    }
    private async void PreviewClick(object sender, RoutedEventArgs e) => await _vm.PreviewAsync();
    private async void CommitClick(object sender, RoutedEventArgs e) => await _vm.CommitAsync();
    private void CancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
    private async void PreviousClick(object sender, RoutedEventArgs e) => await _vm.PageAsync(-1);
    private async void NextClick(object sender, RoutedEventArgs e) => await _vm.PageAsync(1);
    private void TemplateClick(object sender, RoutedEventArgs e) => SaveText($"{_vm.Kind}-template.csv", _vm.Service.GetTemplate(_vm.Kind));
    private void ErrorsClick(object sender, RoutedEventArgs e) => SaveText("导入错误.csv", _vm.Service.GetErrorCsv(_vm.Errors));
    private void SaveText(string name, string content)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = name, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true)); _vm.Feedback = "文件已保存。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _vm.Feedback = "文件保存失败，请检查目录权限或文件占用。"; }
    }
}
