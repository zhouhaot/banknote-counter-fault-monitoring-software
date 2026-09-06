using System.Windows;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;

public partial class OperationsWindow : Window
{
    public OperationsWindow(OperationsViewModel viewModel)
    {
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
}
