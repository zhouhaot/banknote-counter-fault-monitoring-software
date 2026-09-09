using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Simulation;

namespace MoneyCounter.Desktop.ViewModels;

public partial class SimulationViewModel(ISimulationService service, ILogger<SimulationViewModel> logger) : ObservableObject
{
    private Guid _createOperation = Guid.NewGuid(), _resetOperation = Guid.NewGuid();
    private long? _resetTarget;
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<SimulationDetail> Datasets { get; } = [];
    [ObservableProperty] private string seed = "20260826";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private SimulationDetail? selectedDataset;
    [ObservableProperty] private string feedback = "模拟数据仅用于演示，不能作为真实设备运行结论。";
    public bool IsReady => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));
    partial void OnSeedChanged(string value) => _createOperation = Guid.NewGuid();
    public Func<string, bool>? Confirm { get; set; }
    public void Cancel() => _cancellation?.Cancel();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Feedback = "操作已取消，未完成的数据变更已回滚。"; }
        catch (Exception ex) { logger.LogError(ex, "模拟数据操作失败"); Feedback = "操作失败，可重试同一命令检查保存回执。"; }
        finally { _cancellation = null; IsBusy = false; }
    }
    public Task RefreshAsync() => Run(Load);
    private async Task Load(CancellationToken ct)
    {
        var result = await service.ListAsync(ct);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        var selectedId = SelectedDataset?.Id;
        Datasets.Clear(); foreach (var item in result.Value!) Datasets.Add(item);
        SelectedDataset = Datasets.FirstOrDefault(x => x.Id == selectedId);
    }
    public Task CreateAsync() => Run(async ct =>
    {
        if (!long.TryParse(Seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seedValue)) { Feedback = "请输入 Int64 范围内的整数种子。"; return; }
        Feedback = "正在创建独立模拟数据集…";
        var result = await service.CreateAsync(new(_createOperation, seedValue), ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        _createOperation = Guid.NewGuid();
        await RefreshAfterCommit($"模拟集已创建：{result.Value!.Name}");
        SelectedDataset = Datasets.FirstOrDefault(x => x.Id == result.Value.Id);
    });
    public Task ResetAsync() => Run(async ct =>
    {
        if (SelectedDataset is not { } selected) { Feedback = "请先选择要重置的模拟集。"; return; }
        if (Confirm?.Invoke($"确认清除模拟集“{selected.Name}”（编号 {selected.Id}）及其全部演示记录？该操作不可撤销；人工与 CSV 数据不在删除范围。") != true) return;
        if (_resetTarget != selected.Id) { _resetTarget = selected.Id; _resetOperation = Guid.NewGuid(); }
        Feedback = "正在检查关联并清除选定模拟集…";
        var result = await service.ResetAsync(new(_resetOperation, selected.Id), ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        _resetTarget = null; _resetOperation = Guid.NewGuid();
        await RefreshAfterCommit($"模拟集已清除，删除 {result.Value!.DeletedCounts.Values.Sum()} 条记录。人工与 CSV 数据已保留。");
    });
    private async Task RefreshAfterCommit(string message)
    {
        Feedback = message;
        try { await Load(CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "模拟操作已提交但刷新失败"); Feedback = message + " 列表刷新失败，请手动刷新。"; }
    }
}
