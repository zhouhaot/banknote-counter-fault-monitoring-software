using System.Windows;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;

public partial class OperationsWindow : Window
{
    private readonly OperationsViewModel _viewModel;
    private readonly Func<MoneyCounter.Core.Operations.AnomalyDetail, Window> _maintenance;
    public OperationsWindow(OperationsViewModel viewModel, Func<MoneyCounter.Core.Operations.AnomalyDetail, Window> maintenance)
    {
        _viewModel = viewModel; _maintenance = maintenance;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
        Closing += (_, e) =>
        {
            if (viewModel.IsBusy) { e.Cancel = true; viewModel.Feedback = "操作进行中，请等待完成。"; }
            else if (viewModel.HasUnsavedChanges &&
                MessageBox.Show(this, "尚有未保存内容，是否放弃？", "关闭窗口", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private async void ConvertAnomaly_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        if (_viewModel.SelectedAnomaly?.Record is not { Status: "OPEN" } anomaly) { _viewModel.Feedback = "请先选择待处理异常。"; return; }
        var window = _maintenance(anomaly); window.Owner = this; window.ShowDialog();
        await _viewModel.RefreshAsync();
    }
}
