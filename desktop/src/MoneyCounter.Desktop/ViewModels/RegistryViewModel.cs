using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core;
using MoneyCounter.Core.Registry;

namespace MoneyCounter.Desktop.ViewModels;
public partial class RegistryViewModel(IRegistryService service, ILogger<RegistryViewModel> logger) : ObservableObject
{
    public IRegistryService Service => service;
    public ObservableCollection<ModelDetail> Models { get; } = [];
    public ObservableCollection<DeviceDetail> Devices { get; } = [];
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string status = "本地数据 · 就绪";
    [ObservableProperty] private string pageInfo = "";
    [ObservableProperty] private ModelDetail? selectedModel;
    [ObservableProperty] private DeviceDetail? selectedDevice;
    [ObservableProperty] private int section;
    private int _page = 1;
    private long _total;
    private long _refreshVersion;
    public string Title => Section == 0 ? "设备台账" : "型号管理";
    partial void OnSectionChanged(int value) { _page = 1; Search = ""; OnPropertyChanged(nameof(Title)); }
    public void ResetPage() => _page = 1;
    [RelayCommand] private async Task Previous() { if (_page > 1) { _page--; await RefreshAsync(); } }
    [RelayCommand] private async Task Next() { if (_page * 50 < _total) { _page++; await RefreshAsync(); } }
    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion; IsBusy = true;
        try
        {
            if (Section == 0)
            {
                var r = await service.ListDevicesAsync(new(Page: _page, PageSize: 50, Search: Search));
                if (version != _refreshVersion) return;
                if (!r.IsSuccess) { Status = r.Error!.Message; return; }
                Devices.Clear(); foreach (var item in r.Value!.Items) Devices.Add(item); _total = r.Value.TotalCount;
            }
            else
            {
                var r = await service.ListModelsAsync(new(Page: _page, PageSize: 50, Search: Search));
                if (version != _refreshVersion) return;
                if (!r.IsSuccess) { Status = r.Error!.Message; return; }
                Models.Clear(); foreach (var item in r.Value!.Items) Models.Add(item); _total = r.Value.TotalCount;
            }
            PageInfo = $"第 {_page} / {Math.Max(1, (_total + 49) / 50)} 页，共 {_total} 条";
            Status = _total == 0 ? "当前没有符合条件的记录" : "本地数据 · 查询完成";
        }
        catch (Exception ex) { logger.LogError(ex, "Registry query failed"); Status = "查询失败，请检查日志后重试。"; }
        finally { if (version == _refreshVersion) IsBusy = false; }
    }
    public async Task DeactivateAsync()
    {
        IsBusy = true;
        try
        {
            Error? error;
            if (Section == 0 && SelectedDevice is { } d) error = (await service.DeactivateDeviceAsync(new(Guid.NewGuid(), d.Id, d.Revision))).Error;
            else if (Section == 1 && SelectedModel is { } m) error = (await service.DeactivateModelAsync(new(Guid.NewGuid(), m.Id, m.Revision))).Error;
            else { Status = "请先选择一条记录"; return; }
            await RefreshAsync(); Status = error?.Message ?? "停用成功，历史记录仍保留";
        }
        catch (Exception ex) { logger.LogError(ex, "Deactivate failed"); Status = "停用失败，请检查日志。"; }
        finally { IsBusy = false; }
    }
}
