using System.Windows;
using System.Windows.Controls;
using MoneyCounter.Core;
using MoneyCounter.Core.Registry;
using Microsoft.Extensions.Logging;
namespace MoneyCounter.Desktop;
public partial class RegistryEditor : Window
{
    private readonly IRegistryService _service;
    private readonly ILogger<RegistryEditor> _logger;
    private readonly int _section;
    private readonly ModelDetail? _model;
    private readonly DeviceDetail? _device;
    private bool _loading = true, _busy, _dirty, _saved;
    private Guid _operationId = Guid.NewGuid();
    private record ModelOption(long Id, string Label);
    public RegistryEditor(IRegistryService service, ILogger<RegistryEditor> logger, int section, ModelDetail? model, DeviceDetail? device)
    {
        InitializeComponent(); _service = service; _logger = logger; _section = section; _model = model; _device = device;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Title = (section == 0 ? "设备" : "型号") + (model is null && device is null ? "建档" : "编辑");
        ModelFields.Visibility = section == 1 ? Visibility.Visible : Visibility.Collapsed;
        DeviceFields.Visibility = section == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (model is not null) { Manufacturer.Text = model.Manufacturer; ModelName.Text = model.ModelName; RatedLife.Text = model.RatedCountLife?.ToString() ?? ""; Notes.Text = model.Notes; }
        if (device is not null) { AssetCode.Text = device.AssetCode; Location.Text = device.Location; ResponsiblePerson.Text = device.ResponsiblePerson; Notes.Text = device.Notes; CommissionedOn.SelectedDate = device.CommissionedOn?.ToDateTime(TimeOnly.MinValue); PurchasedOn.SelectedDate = device.PurchasedOn?.ToDateTime(TimeOnly.MinValue); }
        Form.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        ModelChoice.SelectionChanged += (_, _) => Changed();
        CommissionedOn.SelectedDateChanged += (_, _) => Changed(); PurchasedOn.SelectedDateChanged += (_, _) => Changed();
        Loaded += LoadModels;
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; ErrorText.Text = "操作进行中，请等待完成。"; }
            else if (_dirty && !_saved && MessageBox.Show(this, "尚有未保存内容，是否放弃？", "关闭表单", MessageBoxButton.YesNo) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
    private void Changed() { if (!_loading) { _dirty = true; _operationId = Guid.NewGuid(); } }
    private async void LoadModels(object sender, RoutedEventArgs e)
    {
        _busy = true; SaveButton.IsEnabled = false;
        try
        {
            if (_section == 0)
            {
                var options = new List<ModelOption>();
                for (var page = 1; ; page++)
                {
                    var result = await _service.ListModelsAsync(new(Page: page, PageSize: 50));
                    if (!result.IsSuccess) { ErrorText.Text = result.Error!.Message; return; }
                    options.AddRange(result.Value!.Items.Where(m => m.IsActive || m.Id == _device?.ModelId).Select(m => new ModelOption(m.Id, m.Manufacturer + " / " + m.ModelName)));
                    if (page * 50 >= result.Value.TotalCount) break;
                }
                ModelChoice.ItemsSource = options;
                if (_device is not null) ModelChoice.SelectedValue = _device.ModelId;
                else if (options.Count > 0) ModelChoice.SelectedIndex = 0;
                if (options.Count == 0) ErrorText.Text = "尚无可用型号，请先在型号管理中建档。";
            }
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Model choices failed"); ErrorText.Text = "型号加载失败，请关闭表单后重试。"; }
        finally { _loading = false; _busy = false; }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; SaveButton.IsEnabled = false; Form.IsEnabled = false; ErrorText.Text = "";
        try
        {
            Error? error;
            if (_section == 1)
            {
                long? life = null;
                if (!string.IsNullOrWhiteSpace(RatedLife.Text))
                {
                    if (!long.TryParse(RatedLife.Text, out var number) || number <= 0) { ErrorText.Text = "额定寿命须为正整数，或留空。"; return; }
                    life = number;
                }
                error = _model is null
                    ? (await _service.CreateModelAsync(new(_operationId, Manufacturer.Text, ModelName.Text, life, Notes.Text))).Error
                    : (await _service.UpdateModelAsync(new(_operationId, _model.Id, _model.Revision, Manufacturer.Text, ModelName.Text, life, Notes.Text))).Error;
            }
            else
            {
                if (ModelChoice.SelectedValue is not long modelId) { ErrorText.Text = "请选择设备型号。"; return; }
                DateOnly? commissioned = CommissionedOn.SelectedDate is { } c ? DateOnly.FromDateTime(c) : null;
                DateOnly? purchased = PurchasedOn.SelectedDate is { } p ? DateOnly.FromDateTime(p) : null;
                error = _device is null
                    ? (await _service.CreateDeviceAsync(new(_operationId, AssetCode.Text, modelId, commissioned, purchased, Location.Text, ResponsiblePerson.Text, Notes.Text))).Error
                    : (await _service.UpdateDeviceAsync(new(_operationId, _device.Id, _device.Revision, AssetCode.Text, modelId, commissioned, purchased, Location.Text, ResponsiblePerson.Text, Notes.Text))).Error;
            }
            if (error is not null)
            {
                ErrorText.Text = error.Code == ErrorCodes.ConcurrentChange ? error.Message + " 请关闭表单后重新选择记录，载入最新内容。" : error.Message;
                Form.IsEnabled = true;
                if (FindName(error.Field ?? "") is Control field) field.Focus();
                return;
            }
            _saved = true; _busy = false; DialogResult = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Registry save failed"); ErrorText.Text = "保存失败，请检查日志后重试；输入已保留。"; }
        finally { _busy = false; SaveButton.IsEnabled = true; Form.IsEnabled = true; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
}
