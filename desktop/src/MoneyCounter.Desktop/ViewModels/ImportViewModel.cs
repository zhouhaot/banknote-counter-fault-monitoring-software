using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoneyCounter.Core.Imports;
using Microsoft.Extensions.Logging;

namespace MoneyCounter.Desktop.ViewModels;

public sealed record ImportKindOption(ImportKind Kind, string Label);
public partial class ImportViewModel(ICsvImportService service, ILogger<ImportViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private CsvPreview? _preview;
    private Guid _operationId = Guid.NewGuid();
    private int _page = 1;
    private long _total;
    public ICsvImportService Service => service;
    public ObservableCollection<CsvIssue> Errors { get; } = [];
    public ObservableCollection<ImportBatchDetail> History { get; } = [];
    public static IReadOnlyList<ImportKindOption> Kinds { get; } = [new(ImportKind.Model, "型号资料"), new(ImportKind.Device, "设备台账"), new(ImportKind.Status, "状态记录")];
    [ObservableProperty] private ImportKind kind;
    [ObservableProperty] private string filePath = "";
    [ObservableProperty] private string feedback = "选择 CSV 文件并预览；全部校验通过后才能提交。";
    [ObservableProperty] private string previewSummary = "尚未预览";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private bool isBusy;
    public bool IsReady => !IsBusy;
    public bool CanCommit => IsReady && _preview?.CanCommit == true;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(CanCommit)); }
    partial void OnKindChanged(ImportKind value) => InvalidatePreview();
    partial void OnFilePathChanged(string value) => InvalidatePreview();
    private void InvalidatePreview()
    {
        _preview = null; _operationId = Guid.NewGuid(); Errors.Clear(); PreviewSummary = "文件或类型已改变，请重新预览。";
        OnPropertyChanged(nameof(CanCommit));
    }
    public void Cancel() => _cancellation?.Cancel();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Feedback = "操作已取消，未完成的导入已回滚。"; }
        catch (Exception ex) { logger.LogError(ex, "CSV 操作失败"); Feedback = "操作失败。请重试；相同导入命令会检查保存回执，避免重复写入。"; }
        finally { _cancellation = null; IsBusy = false; }
    }
    public Task PreviewAsync() => Run(async ct =>
    {
        _preview = null; Errors.Clear(); Feedback = "正在读取文件并校验引用与读数…";
        var result = await service.PreviewAsync(Kind, FilePath, ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; PreviewSummary = "预览未通过"; return; }
        _preview = result.Value!; _operationId = Guid.NewGuid();
        foreach (var issue in _preview.Errors) Errors.Add(issue);
        PreviewSummary = $"{_preview.FileName} · {_preview.RowCount} 条数据 · {_preview.ByteCount} 字节\nSHA256：{_preview.Sha256}";
        Feedback = _preview.CanCommit ? "预览通过。提交时将再次检查文件内容和当前数据库。" : $"发现 {Errors.Count} 项错误，请修改文件后重新预览。";
    });
    public Task CommitAsync() => Run(async ct =>
    {
        if (_preview?.CanCommit != true) { Feedback = "请先完成无错误预览。"; return; }
        Feedback = "正在重新校验并提交整批数据…";
        var result = await service.CommitAsync(_operationId, Kind, FilePath, _preview.Sha256, ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        Errors.Clear(); foreach (var issue in result.Value!.Errors) Errors.Add(issue);
        if (!result.Value.IsCommitted) { _preview = null; Feedback = "提交校验未通过，未写入业务记录。请重新预览。"; return; }
        _preview = null; _operationId = Guid.NewGuid(); _page = 1;
        Feedback = $"导入成功，共 {result.Value.Batch!.RowCount} 条。";
        try { await LoadHistory(CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "导入已提交但批次刷新失败"); Feedback += "批次刷新失败，可点击刷新查看。"; }
    });
    public Task RefreshAsync() => Run(LoadHistory);
    public Task PageAsync(int delta) => Run(async ct => { if (delta < 0 && _page > 1 || delta > 0 && _page * 50L < _total) { _page += delta; await LoadHistory(ct); } });
    private async Task LoadHistory(CancellationToken ct)
    {
        var result = await service.ListHistoryAsync(_page, 50, ct);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        History.Clear(); foreach (var batch in result.Value!.Items) History.Add(batch);
        _total = result.Value.TotalCount; PageInfo = $"第 {_page} 页，共 {_total} 批";
    }
}
