using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Backup;

namespace MoneyCounter.Desktop.ViewModels;

public partial class BackupViewModel(IBackupService service, ILogger<BackupViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<BackupDetail> Backups { get; } = [];
    [ObservableProperty] private BackupDetail? selectedBackup;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string feedback = "备份会先等待当前数据库操作结束，再创建一致快照。";
    public bool IsReady => !IsBusy;
    public Func<string, bool>? Confirm { get; set; }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReady));
    public void Cancel() => _cancellation?.Cancel();

    public Task RefreshAsync() => Run(async ct =>
    {
        var result = await service.ListAsync(ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        var selected = SelectedBackup?.FileName;
        Backups.Clear(); foreach (var item in result.Value!) Backups.Add(item);
        SelectedBackup = Backups.FirstOrDefault(x => x.FileName == selected);
        Feedback = Backups.Count == 0 ? "尚无备份。" : $"已加载 {Backups.Count} 个备份。";
    });

    public Task CreateAsync() => Run(async ct =>
    {
        Feedback = "正在等待数据库空闲并创建备份…";
        var result = await service.CreateAsync(ct);
        if (!result.IsSuccess) { Feedback = result.Error!.Message; return; }
        await ReloadAfterWrite(result.Value!.FileName, ct);
        Feedback = "备份已创建并完成完整性与外键校验。";
    });

    public Task RestoreAsync() => Run(async ct =>
    {
        if (SelectedBackup is not { } backup) { Feedback = "请先选择一个备份。"; return; }
        if (Confirm?.Invoke($"确认恢复备份“{backup.FileName}”？恢复会先校验备份，并自动保留回退副本；恢复期间不能进行其他数据操作。") != true) return;
        Feedback = "正在校验备份并执行可回退恢复…";
        var result = await service.RestoreAsync(backup.FileName, ct);
        Feedback = result.IsSuccess ? result.Value!.Message : result.Error!.Message;
        if (result.IsSuccess) await ReloadAfterWrite(backup.FileName, ct);
    });

    private async Task ReloadAfterWrite(string? selected, CancellationToken ct)
    {
        var list = await service.ListAsync(ct);
        if (!list.IsSuccess) throw new InvalidOperationException(list.Error!.Message);
        Backups.Clear(); foreach (var item in list.Value!) Backups.Add(item);
        SelectedBackup = Backups.FirstOrDefault(x => x.FileName == selected);
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Feedback = "操作已取消，原数据库未被替换。"; }
        catch (Exception ex) { logger.LogError(ex, "备份或恢复操作失败"); Feedback = "操作失败，已保留当前数据库；请查看备份状态后重试。"; }
        finally { _cancellation = null; IsBusy = false; }
    }
}
