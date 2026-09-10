using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace MoneyCounter.UiTests;

internal static class Program
{
    private const int TimeoutMilliseconds = 15_000;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            Directory.CreateDirectory(options.EvidenceDirectory);
            var run = new SmokeRun(options);
            run.Execute();
            run.WriteReport(true, null);
            Console.WriteLine($"PASS: native UI smoke evidence: {options.EvidenceDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
    }

    private sealed class SmokeRun(Options options)
    {
        private readonly List<string> _steps = [];
        private Process? _process;
        private readonly string _manufacturer = "UIA 厂商";
        private readonly string _modelName = "UIA-100";
        private readonly string _assetCode = "UIA-ASSET-001";

        public void Execute()
        {
            try
            {
                var main = Start();
                CreateModel(main);
                CreateDevice(main);
                QueryDevice(main);
                VerifyDuplicateModel(main);
                Invoke(Find(main, "NavDevices"));
                WaitForStatus(main, "本地数据 · 查询完成");
                ExerciseOperations(main);
                CloseApplication(main);

                main = Start();
                VerifyPersistedModel(main);
                VerifyPersistedDevice(main);
                VerifyPersistedOperations(main);
                VerifyPersistedMaintenance(main);
                VerifyPersistedInventory(main);
                ExerciseImports(main);
                ExerciseSimulation(main);
                ExerciseAnalytics(main);
                CaptureWindow(main, Path.Combine(options.EvidenceDirectory, "persisted-device.png"));
                CloseApplication(main);
            }
            catch (Exception ex)
            {
                if (_process is not null && !_process.HasExited)
                {
                    try
                    {
                        var failedWindow = FindWindow(_process.Id, "MainWindow");
                        if (failedWindow is not null)
                        {
                            CaptureWindow(failedWindow, Path.Combine(options.EvidenceDirectory, "failure.png"));
                            File.WriteAllText(Path.Combine(options.EvidenceDirectory, "failure-ui.txt"), string.Join("\n", failedWindow.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(x => $"{x.Current.ControlType.ProgrammaticName} {x.Current.AutomationId}: {x.Current.Name}")));
                        }
                    }
                    catch (Exception diagnostic) { Console.Error.WriteLine("Failure capture unavailable: " + diagnostic.Message); }
                    try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5_000); } catch { }
                }
                WriteReport(false, ex);
                throw;
            }
        }

        public void WriteReport(bool passed, Exception? error)
        {
            var report = new { passed, executable = options.Executable, dataDirectory = options.DataDirectory, steps = _steps, error = error?.ToString() };
            File.WriteAllText(Path.Combine(options.EvidenceDirectory, "ui-smoke-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void ExerciseAnalytics(AutomationElement main)
        {
            Invoke(Find(main, "NavAnalytics"));
            var window = WaitForWindow(_process!.Id, "AnalyticsWindow");
            WaitUntil(() => Find(window, "AnalyticsFeedback").Current.Name.StartsWith("统计已刷新", StringComparison.Ordinal), "analytics initialized");
            WaitForGridName(window, "AnalyticsLifeGrid", "UIA-CSV-DEVICE");
            SetValue(Find(window, "AnalyticsAsset"), "UIA-CSV-DEVICE");
            Invoke(Find(window, "RefreshAnalytics"));
            WaitUntil(() => Find(window, "RefreshAnalytics").Current.IsEnabled && Find(window, "AnalyticsLifeGrid").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Count == 1, "analytics exact device filter");
            WaitForGridName(window, "AnalyticsLifeGrid", "200");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "analytics-life.png"));
            SetValue(Find(window, "AnalyticsAsset"), "DOES-NOT-EXIST");
            Invoke(Find(window, "RefreshAnalytics"));
            WaitUntil(() => Find(window, "AnalyticsFeedback").Current.Name.Contains("未找到", StringComparison.Ordinal), "analytics unknown asset rejected");
            if (Find(window, "AnalyticsLifeGrid").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Count != 0) throw new InvalidOperationException("Analytics failure retained stale rows.");
            SetValue(Find(window, "AnalyticsAsset"), "");
            Invoke(Find(window, "RefreshAnalytics"));
            WaitUntil(() => Find(window, "AnalyticsFeedback").Current.Name.StartsWith("统计已刷新", StringComparison.Ordinal), "analytics recovered");
            ((SelectionItemPattern)Find(window, "AnalyticsTrendTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            WaitUntil(() => Find(window, "AnalyticsTrendSummary").Current.Name == "所选范围没有故障记录。", "empty trend explicitly described");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "analytics-trend.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Verified native analytics filtering, latest imported count, clear error state and honest empty trend.");
        }

        private AutomationElement Start()
        {
            var info = new ProcessStartInfo(options.Executable) { UseShellExecute = false };
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(options.DataDirectory);
            _process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start the desktop executable.");
            var main = WaitForWindow(_process.Id, "MainWindow");
            WaitUntil(() => Find(main, "StatusText").Current.Name is "当前没有符合条件的记录" or "本地数据 · 查询完成", "initial registry query");
            _steps.Add("Started actual desktop application with an isolated absolute data directory.");
            return main;
        }

        private void CreateModel(AutomationElement main)
        {
            Invoke(Find(main, "NavModels"));
            WaitForStatus(main, "当前没有符合条件的记录");
            Invoke(Find(main, "AddRecord"));
            var editor = WaitForWindow(_process!.Id, "RegistryEditor");
            SetValue(Find(editor, "Manufacturer"), _manufacturer);
            SetValue(Find(editor, "ModelName"), _modelName);
            SetValue(Find(editor, "RatedLife"), "1000000");
            Invoke(Find(editor, "SaveRecord"));
            WaitUntil(() => FindWindow(_process!.Id, "RegistryEditor") is null, "model editor to close after save");
            WaitForName(main, _modelName);
            _steps.Add("Created a model through the model-management editor.");
        }

        private void CreateDevice(AutomationElement main)
        {
            Invoke(Find(main, "NavDevices"));
            WaitForStatus(main, "当前没有符合条件的记录");
            Invoke(Find(main, "AddRecord"));
            var editor = WaitForWindow(_process!.Id, "RegistryEditor");
            SetValue(Find(editor, "AssetCode"), _assetCode);
            var choice = Find(editor, "ModelChoice");
            WaitUntil(() => choice.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern) && ((SelectionPattern)pattern).Current.GetSelection().Length == 1, "default model selection");
            SetValue(Find(editor, "Location"), "UI 自动化实验室");
            SetValue(Find(editor, "ResponsiblePerson"), "UIA Tester");
            Invoke(Find(editor, "SaveRecord"));
            WaitUntil(() => FindWindow(_process!.Id, "RegistryEditor") is null, "device editor to close after save");
            WaitForName(main, _assetCode);
            _steps.Add("Created a device through the device-management editor.");
        }

        private void QueryDevice(AutomationElement main)
        {
            SetValue(Find(main, "SearchBox"), "NO-MATCH-" + Guid.NewGuid().ToString("N"));
            Invoke(Find(main, "SearchButton"));
            WaitForStatus(main, "当前没有符合条件的记录");
            if (Find(main, "DevicesGrid").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, _assetCode)) is not null)
                throw new InvalidOperationException("Nonmatching query retained the device row.");
            SetValue(Find(main, "SearchBox"), _assetCode);
            Invoke(Find(main, "SearchButton"));
            WaitForStatus(main, "本地数据 · 查询完成");
            WaitForName(Find(main, "DevicesGrid"), _assetCode);
            _steps.Add("Queried the newly created device through the main-window search controls.");
        }

        private void VerifyDuplicateModel(AutomationElement main)
        {
            Invoke(Find(main, "NavModels"));
            WaitForStatus(main, "本地数据 · 查询完成");
            Invoke(Find(main, "AddRecord"));
            var editor = WaitForWindow(_process!.Id, "RegistryEditor");
            SetValue(Find(editor, "Manufacturer"), _manufacturer);
            SetValue(Find(editor, "ModelName"), _modelName);
            Invoke(Find(editor, "SaveRecord"));
            var error = Find(editor, "ValidationError");
            WaitUntil(() => (error.Current.Name ?? string.Empty).Contains("型号已存在", StringComparison.Ordinal), "duplicate model validation message");
            CaptureWindow(editor, Path.Combine(options.EvidenceDirectory, "duplicate-validation.png"));
            Invoke(Find(editor, "CancelEdit"));
            AutomationElement? discard = null;
            WaitUntil(() => (discard = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"))) is not null, "unsaved-change confirmation");
            Invoke(discard!);
            WaitUntil(() => FindWindow(_process!.Id, "RegistryEditor") is null, "duplicate editor to close");
            _steps.Add("Verified duplicate-model validation in the real editor without accepting a false save.");
        }

        private void VerifyPersistedModel(AutomationElement main)
        {
            Invoke(Find(main, "NavModels"));
            WaitForStatus(main, "本地数据 · 查询完成");
            WaitForName(Find(main, "ModelsGrid"), _modelName);
            SetValue(Find(main, "SearchBox"), "NO-MATCH-" + Guid.NewGuid().ToString("N"));
            Invoke(Find(main, "SearchButton"));
            WaitForStatus(main, "当前没有符合条件的记录");
            SetValue(Find(main, "SearchBox"), _modelName);
            Invoke(Find(main, "SearchButton"));
            WaitForStatus(main, "本地数据 · 查询完成");
            WaitForName(Find(main, "ModelsGrid"), _modelName);
            _steps.Add("Reopened the application and verified the model persisted.");
        }

        private void VerifyPersistedDevice(AutomationElement main)
        {
            Invoke(Find(main, "NavDevices"));
            WaitForStatus(main, "本地数据 · 查询完成");
            WaitForName(Find(main, "DevicesGrid"), _assetCode);
            QueryDevice(main);
            _steps.Add("Reopened the application and verified the device persisted.");
        }

        private void CloseApplication(AutomationElement main)
        {
            ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _process!.WaitForExit(TimeoutMilliseconds);
            if (!_process.HasExited) throw new InvalidOperationException("Desktop application did not exit after its window was closed.");
            _steps.Add("Closed the real application cleanly.");
        }

        private AutomationElement OpenOperations(AutomationElement main)
        {
            var row = Find(main, "DevicesGrid").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)) ?? throw new InvalidOperationException("Device row missing.");
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Invoke(Find(main, "NavOperations"));
            var window = WaitForWindow(_process!.Id, "OperationsWindow");
            WaitUntil(() => Find(window, "OperationsFeedback").Current.Name == "历史记录已刷新", "operations history loaded");
            return window;
        }

        private void ExerciseOperations(AutomationElement main)
        {
            var window = OpenOperations(main);
            void Save(string time, string count, string feedback)
            {
                SetValue(Find(window, "RecordedAt"), time);
                SetValue(Find(window, "CumulativeCount"), count);
                Invoke(Find(window, "SaveStatus"));
                WaitUntil(() => Find(window, "OperationsFeedback").Current.Name.Contains(feedback, StringComparison.Ordinal), feedback);
                if (feedback == "状态记录已保存") WaitForName(Find(window, "StatusHistory"), count);
            }
            Save("2026-01-01 08:00:00", "100", "状态记录已保存");
            Save("2026-01-01 10:00:00", "300", "状态记录已保存");
            Save("2026-01-01 09:00:00", "301", "累计读数大于后一条记录");
            if (((ValuePattern)Find(window, "CumulativeCount").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "301") throw new InvalidOperationException("Failed save lost input.");
            Save("2026-01-01 09:00:00", "200", "状态记录已保存");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "status-history.png"));
            ((SelectionItemPattern)Find(window, "AnomalyTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            SetValue(Find(window, "DiscoveredAt"), "2026-01-01 08:00:00");
            SetValue(Find(window, "AnomalyDescription"), "UIA 卡钞异常");
            Invoke(Find(window, "SaveAnomaly"));
            WaitUntil(() => Find(window, "OperationsFeedback").Current.Name == "异常已登记", "anomaly created");
            var row = Find(window, "AnomalyHistory").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))!;
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            SetValue(Find(window, "ClosedAt"), "2026-01-01 09:00:00");
            SetValue(Find(window, "HandlingNotes"), "UIA 清理并确认恢复");
            Invoke(Find(window, "CloseAnomaly"));
            WaitUntil(() => Find(window, "OperationsFeedback").Current.Name == "异常已关闭，历史记录已保留", "anomaly closed");
            WaitForName(Find(window, "AnomalyHistory"), "已关闭");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "anomaly-closed.png"));
            ExerciseMaintenance(main, window);
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Recorded status chronology, rejected backward historical reading with input preserved, and created/closed an anomaly in native UI.");
        }

        private void VerifyPersistedOperations(AutomationElement main)
        {
            var window = OpenOperations(main);
            WaitForName(Find(window, "StatusHistory"), "300");
            ((SelectionItemPattern)Find(window, "AnomalyTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            WaitForName(Find(window, "AnomalyHistory"), "已关闭");
            WaitForName(Find(window, "AnomalyHistory"), "UIA 清理并确认恢复");
            ((SelectionItemPattern)Find(window, "StatusTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            SetValue(Find(window, "StatusNotes"), "UIA 未保存内容");
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            AutomationElement? no = null;
            WaitUntil(() => (no = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "7"))) is not null, "unsaved operations confirmation");
            Invoke(no!);
            if (((ValuePattern)Find(window, "StatusNotes").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "UIA 未保存内容") throw new InvalidOperationException("Cancel close lost status input.");
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            AutomationElement? yes = null;
            WaitUntil(() => (yes = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"))) is not null, "discard operations confirmation");
            Invoke(yes!);
            _steps.Add("Reopened and verified persisted status history and closed anomaly handling notes.");
            _steps.Add("Verified unsaved status input survives cancelling window closure, then explicitly discarded it.");
        }

        private void ExerciseMaintenance(AutomationElement main, AutomationElement operations)
        {
            SetValue(Find(operations, "DiscoveredAt"), "2026-01-01 11:00:00");
            SetValue(Find(operations, "AnomalyDescription"), "UIA 待转故障");
            Invoke(Find(operations, "SaveAnomaly"));
            WaitUntil(() => Find(operations, "OperationsFeedback").Current.Name == "异常已登记", "second anomaly created");
            var anomaly = Find(operations, "AnomalyHistory").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))!;
            ((SelectionItemPattern)anomaly.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Invoke(Find(operations, "ConvertAnomaly"));
            var window = WaitForWindow(_process!.Id, "MaintenanceWindow");
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "已刷新", "maintenance initialized");
            Invoke(Find(window, "CreateFault"));
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "故障已登记", "anomaly converted");
            ((SelectionItemPattern)Find(window, "FaultDetailTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            SetValue(Find(window, "FaultActionAt"), "2026-01-01 11:10:00");
            Invoke(Find(window, "StartFault"));
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "已开始处理", "fault started");
            SetValue(Find(window, "FaultClosedAt"), "2026-01-01 11:30:00");
            SetValue(Find(window, "FaultFinalResult"), "UIA 测试确认恢复");
            Invoke(Find(window, "CloseFault"));
            ConfirmYes(main);
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name.Contains("关闭前必须登记维修记录", StringComparison.Ordinal), "close without repair rejected");
            SetValue(Find(window, "RepairAt"), "2026-01-01 11:20:00");
            SetValue(Find(window, "RepairAction"), "UIA 清理传感器");
            SetValue(Find(window, "RepairResult"), "UIA 点钞测试正常");
            SetValue(Find(window, "RepairTechnician"), "UIA 维修员");
            Invoke(Find(window, "SaveRepair"));
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "维修记录已保存", "repair saved");
            ExerciseInventory(main, window);
            WaitForName(Find(window, "RepairGrid"), "UIA 清理传感器");
            Invoke(Find(window, "CloseFault"));
            ConfirmYes(main);
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "故障已关闭", "fault closed");
            if (Find(window, "SaveRepair").Current.IsEnabled || Find(window, "DeleteRepair").Current.IsEnabled) throw new InvalidOperationException("Closed fault permits repair editing.");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "fault-closed.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            WaitUntil(() => Find(operations, "OperationsFeedback").Current.Name == "历史记录已刷新", "conversion history refreshed");
            WaitForName(Find(operations, "AnomalyHistory"), "已转故障");
            _steps.Add("Converted anomaly to fault, started handling, rejected closure without repair, saved repair and closed with immutable history.");
        }

        private void ExerciseInventory(AutomationElement main, AutomationElement maintenance)
        {
            Invoke(Find(maintenance, "FaultInventory"));
            var window = WaitForWindow(_process!.Id, "InventoryWindow");
            WaitUntil(() => Find(window, "InventoryFeedback").Current.Name == "已刷新库存与流水", "inventory loaded");
            SetValue(Find(window, "ConsumableName"), "UIA 清洁布");
            SetValue(Find(window, "ConsumableUnit"), "包");
            Invoke(Find(window, "SaveConsumable"));
            WaitForGridName(window, "ConsumableGrid", "UIA 清洁布");
            SelectFirstRow(Find(window, "ConsumableGrid"));
            SetValue(Find(window, "MovementQuantity"), "1.25");
            SetValue(Find(window, "MovementTime"), "2026-01-01 11:21:00");
            SetValue(Find(window, "MovementReason"), "UIA 领用");
            Invoke(Find(window, "PostMovement"));
            WaitUntil(() => Find(window, "InventoryFeedback").Current.Name.Contains("库存不足", StringComparison.Ordinal), "insufficient stock rejected");
            if (((ValuePattern)Find(window, "MovementQuantity").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "1.25") throw new InvalidOperationException("Inventory rejection lost input.");
            SetValue(Find(window, "ConsumableName"), "UIA 备用耗材");
            SetValue(Find(window, "ConsumableUnit"), "包");
            Invoke(Find(window, "SaveConsumable"));
            WaitForGridName(window, "ConsumableGrid", "UIA 备用耗材");
            SetValue(Find(window, "ConsumableSearch"), "UIA 备用耗材");
            Invoke(Find(window, "SearchConsumables"));
            WaitUntil(() => Find(window, "ConsumableGrid").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Count == 1, "filtered inventory catalog");
            var switchTarget = System.Threading.Tasks.Task.Run(() => SelectFirstRow(Find(window, "ConsumableGrid")));
            nint noButton = 0;
            WaitUntil(() =>
            {
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var pid);
                    if (pid == _process!.Id) noButton = GetDlgItem(handle, 7);
                    return noButton == 0;
                }, 0);
                return noButton != 0;
            }, "draft target change confirmation");
            PostMessage(noButton, 0x00F5, 0, 0);
            if (!switchTarget.Wait(TimeoutMilliseconds)) throw new TimeoutException("Inventory selection did not finish after confirmation.");
            if (((SelectionPattern)Find(window, "ConsumableGrid").GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().Length != 0) throw new InvalidOperationException("Declined transfer retained the other consumable selection.");
            if (((ValuePattern)Find(window, "MovementQuantity").GetCurrentPattern(ValuePattern.Pattern)).Current.Value != "1.25") throw new InvalidOperationException("Cancelled target switch lost quantity.");
            SetValue(Find(window, "ConsumableSearch"), "UIA 清洁布");
            Invoke(Find(window, "SearchConsumables"));
            WaitForGridName(window, "ConsumableGrid", "UIA 清洁布");
            SelectFirstRow(Find(window, "ConsumableGrid"));
            SelectChoice(Find(window, "MovementType"), "入库");
            SetValue(Find(window, "MovementQuantity"), "10");
            SetValue(Find(window, "MovementReason"), "UIA 入库");
            Invoke(Find(window, "PostMovement"));
            WaitForGridName(window, "ConsumableGrid", "10.00");
            SelectChoice(Find(window, "MovementType"), "领用（扣减）");
            SetValue(Find(window, "MovementQuantity"), "1.25");
            SetValue(Find(window, "MovementTime"), "2026-01-01 11:22:00");
            SetValue(Find(window, "MovementReason"), "UIA 领用");
            Invoke(Find(window, "PostMovement"));
            WaitForGridName(window, "ConsumableGrid", "8.75");
            ((SelectionItemPattern)Find(window, "InventoryLedgerTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            SelectFirstRow(Find(window, "InventoryLedger"));
            SetValue(Find(window, "MovementTime"), "2026-01-01 11:23:00");
            SetValue(Find(window, "MovementReason"), "UIA 冲正领用");
            Invoke(Find(window, "ReverseMovement"));
            ConfirmYes(main);
            WaitForName(Find(window, "InventoryLedger"), "UIA 冲正领用");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "inventory-reversal.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Verified insufficient-stock rejection preserves input and repair; received 10, issued 1.25 and reversed issue while retaining ledger history.");
        }

        private void VerifyPersistedInventory(AutomationElement main)
        {
            Invoke(Find(main, "NavInventory"));
            var window = WaitForWindow(_process!.Id, "InventoryWindow");
            WaitUntil(() => Find(window, "InventoryFeedback").Current.Name == "已刷新库存与流水", "persisted inventory loaded");
            WaitForGridName(window, "ConsumableGrid", "10.00");
            ((SelectionItemPattern)Find(window, "InventoryLedgerTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            foreach (var reason in new[] { "UIA 入库", "UIA 领用", "UIA 冲正领用" }) WaitForName(Find(window, "InventoryLedger"), reason);
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Reopened inventory and verified balance 10.00 with all three immutable ledger entries.");
        }

        private void ExerciseImports(AutomationElement main)
        {
            var csv = Path.Combine(options.EvidenceDirectory, "UIA-import.csv");
            File.WriteAllText(csv, "manufacturer,model_name,rated_count_life,notes\r\nUIA CSV厂商,UIA-CSV-MODEL,abc,无效寿命\r\n", new System.Text.UTF8Encoding(true));
            Invoke(Find(main, "NavImports"));
            var window = WaitForWindow(_process!.Id, "ImportWindow");
            WaitUntil(() => Find(window, "PreviewCsv").Current.IsEnabled, "import page ready");
            SetValue(Find(window, "CsvPath"), csv);
            Invoke(Find(window, "PreviewCsv"));
            WaitUntil(() => Find(window, "ImportFeedback").Current.Name.Contains("发现", StringComparison.Ordinal), "invalid import preview");
            if (Find(window, "CommitCsv").Current.IsEnabled) throw new InvalidOperationException("Invalid CSV permits commit.");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "csv-errors.png"));
            File.WriteAllText(csv, "manufacturer,model_name,rated_count_life,notes\r\nUIA CSV厂商,UIA-CSV-MODEL,100000,测试导入\r\n", new System.Text.UTF8Encoding(true));
            Invoke(Find(window, "PreviewCsv"));
            WaitUntil(() => Find(window, "CommitCsv").Current.IsEnabled, "valid import preview");
            File.AppendAllText(csv, "UIA CSV厂商,UIA-CHANGED,200000,预览后修改\r\n");
            Invoke(Find(window, "CommitCsv"));
            WaitUntil(() => Find(window, "ImportFeedback").Current.Name.Contains("文件内容已改变", StringComparison.Ordinal), "changed CSV rejected");
            File.WriteAllText(csv, "manufacturer,model_name,rated_count_life,notes\r\nUIA CSV厂商,UIA-CSV-MODEL,100000,测试导入\r\n", new System.Text.UTF8Encoding(true));
            Invoke(Find(window, "PreviewCsv"));
            WaitUntil(() => Find(window, "CommitCsv").Current.IsEnabled, "corrected CSV revalidated");
            Invoke(Find(window, "CommitCsv"));
            WaitUntil(() => Find(window, "ImportFeedback").Current.Name.StartsWith("导入成功", StringComparison.Ordinal), "CSV committed");
            ((SelectionItemPattern)Find(window, "ImportHistoryTab").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            WaitForName(Find(window, "ImportHistory"), "UIA-import.csv");
            void ImportFile(string kind, string name, string content)
            {
                var path = Path.Combine(options.EvidenceDirectory, name);
                File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
                SelectChoice(Find(window, "ImportKind"), kind);
                SetValue(Find(window, "CsvPath"), path);
                Invoke(Find(window, "PreviewCsv"));
                WaitUntil(() => Find(window, "CommitCsv").Current.IsEnabled, "valid " + kind + " preview");
                Invoke(Find(window, "CommitCsv"));
                WaitUntil(() => Find(window, "ImportFeedback").Current.Name.StartsWith("导入成功", StringComparison.Ordinal), kind + " committed");
                WaitForName(Find(window, "ImportHistory"), name);
            }
            ImportFile("设备台账", "UIA-devices.csv", "asset_code,manufacturer,model_name,commissioned_on,purchased_on,location,responsible_person,notes\r\nUIA-CSV-DEVICE,UIA CSV厂商,UIA-CSV-MODEL,2026-01-01,,测试地点,测试负责人,\r\n");
            ImportFile("状态记录", "UIA-statuses.csv", "asset_code,recorded_at,status,cumulative_count,notes\r\nUIA-CSV-DEVICE,2026-01-01T08:00:00+08:00,RUNNING,100,第一条\r\nUIA-CSV-DEVICE,2026-01-01T09:00:00+08:00,STOPPED,200,第二条\r\n");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "csv-committed.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Rejected invalid CSV and changed-after-preview content; imported model, device and statuses and verified all three committed batches in native UI.");
        }

        private void ExerciseSimulation(AutomationElement main)
        {
            Invoke(Find(main, "NavSimulation"));
            var window = WaitForWindow(_process!.Id, "SimulationWindow");
            Invoke(Find(window, "CreateSimulation"));
            WaitUntil(() => Find(window, "SimulationFeedback").Current.Name.StartsWith("模拟集已创建", StringComparison.Ordinal), "simulation created");
            WaitForName(Find(window, "SimulationGrid"), "模拟集 20260826");
            SelectFirstRow(Find(window, "SimulationGrid"));
            Invoke(Find(window, "ResetSimulation"));
            ConfirmYes(main);
            WaitUntil(() => Find(window, "SimulationFeedback").Current.Name.StartsWith("模拟集已清除", StringComparison.Ordinal), "simulation reset");
            WaitUntil(() => Find(window, "CreateSimulation").Current.IsEnabled && Find(window, "SimulationGrid").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Count == 0, "simulation list cleared after refresh");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "simulation-reset.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Invoke(Find(main, "NavModels"));
            SetValue(Find(main, "SearchBox"), "UIA-CSV-MODEL");
            Invoke(Find(main, "SearchButton"));
            WaitForName(Find(main, "ModelsGrid"), "UIA-CSV-MODEL");
            SetValue(Find(main, "SearchBox"), _modelName);
            Invoke(Find(main, "SearchButton"));
            WaitForName(Find(main, "ModelsGrid"), _modelName);
            _steps.Add("Created and reset isolated simulation dataset through native UI; confirmed imported and manual models retained.");
        }

        private static void SelectFirstRow(AutomationElement grid)
        {
            var row = grid.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)) ?? throw new InvalidOperationException("Grid row missing.");
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }

        private static void SelectChoice(AutomationElement combo, string name)
        {
            var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expand.Expand();
            var item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().FirstOrDefault(x => x.Current.Name == name || x.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) is not null) ?? throw new InvalidOperationException("Choice missing: " + name);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            expand.Collapse();
        }

        private static void ConfirmYes(AutomationElement main)
        {
            AutomationElement? yes = null;
            WaitUntil(() => (yes = main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"))) is not null, "confirmation dialog");
            Invoke(yes!);
        }

        private void VerifyPersistedMaintenance(AutomationElement main)
        {
            WaitUntil(() => FindWindow(_process!.Id, "OperationsWindow") is null, "operations window closed");
            Invoke(Find(main, "NavMaintenance"));
            var window = WaitForWindow(_process!.Id, "MaintenanceWindow");
            WaitUntil(() => Find(window, "MaintenanceFeedback").Current.Name == "已刷新", "persisted faults loaded");
            var row = Find(window, "FaultGrid").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))!;
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Invoke(Find(window, "OpenFault"));
            WaitUntil(() => Find(window, "FaultDetailText").Current.Name.Contains("UIA 测试确认恢复", StringComparison.Ordinal), "persisted fault conclusion");
            WaitForName(Find(window, "RepairGrid"), "UIA 清理传感器");
            if (Find(window, "SaveRepair").Current.IsEnabled) throw new InvalidOperationException("Reopened closed fault is editable.");
            CaptureWindow(window, Path.Combine(options.EvidenceDirectory, "persisted-fault.png"));
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            _steps.Add("Reopened closed fault and verified persisted repair, final conclusion and read-only controls.");
        }
    }

    private static AutomationElement WaitForWindow(int processId, string automationId)
    {
        AutomationElement? window = null;
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < TimeoutMilliseconds)
        {
            if ((window = FindWindow(processId, automationId)) is not null) return window;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for window '{automationId}'. Top-level windows for process {processId}: {DescribeWindows(processId)}");
    }

    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out int processId);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int controlId);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    private static AutomationElement? FindWindow(int processId, string automationId)
    {
        var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        foreach (AutomationElement candidate in candidates)
        {
            if (candidate.Current.AutomationId == automationId) return candidate;
            var owned = candidate.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window), new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));
            if (owned is not null) return owned;
            if (automationId == "RegistryEditor" && candidate.Current.ControlType == ControlType.Window && candidate.Current.Name.EndsWith("建档", StringComparison.Ordinal)) return candidate;
        }
        return null;
    }

    private static string DescribeWindows(int processId)
    {
        var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        return string.Join("; ", candidates.Cast<AutomationElement>().Select(x => $"id={x.Current.AutomationId},name={x.Current.Name},type={x.Current.ControlType.ProgrammaticName}"));
    }

    private static AutomationElement Find(AutomationElement root, string automationId) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)) ?? throw new InvalidOperationException($"Automation element '{automationId}' was not found.");

    private static void Invoke(AutomationElement element)
    {
        WaitUntil(() => element.Current.IsEnabled, "enabled control " + element.Current.AutomationId);
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) throw new InvalidOperationException($"'{element.Current.AutomationId}' does not support InvokePattern.");
        ((InvokePattern)pattern).Invoke();
    }

    private static void SetValue(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) throw new InvalidOperationException($"'{element.Current.AutomationId}' does not support ValuePattern.");
        ((ValuePattern)pattern).SetValue(value);
    }

    private static void WaitForGridName(AutomationElement window, string gridId, string expected) => WaitUntil(() => Find(window, gridId).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, expected)) is not null, "grid content " + expected);

    private static void WaitForName(AutomationElement root, string expected) => WaitUntil(() => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, expected)) is not null, $"text '{expected}'");

    private static void WaitForStatus(AutomationElement main, string expected) => WaitUntil(() => Find(main, "StatusText").Current.Name == expected, $"status '{expected}'");

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < TimeoutMilliseconds)
        {
            try { if (condition()) return; }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Automation element '", StringComparison.Ordinal)) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static void CaptureWindow(AutomationElement window, string path)
    {
        var rect = window.Current.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("Cannot capture a window without a visible bounding rectangle.");
        using var image = new Bitmap((int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height));
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen((int)rect.Left, (int)rect.Top, 0, 0, image.Size, CopyPixelOperation.SourceCopy);
        image.Save(path, ImageFormat.Png);
    }

    private sealed record Options(string Executable, string DataDirectory, string EvidenceDirectory)
    {
        public static Options Parse(string[] args)
        {
            string? exe = null, evidence = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--exe" && ++i < args.Length) exe = args[i];
                else if (args[i] == "--evidence" && ++i < args.Length) evidence = args[i];
                else throw new ArgumentException("Usage: --exe <absolute executable path> --evidence <absolute evidence directory>");
            }
            if (string.IsNullOrWhiteSpace(exe) || string.IsNullOrWhiteSpace(evidence) || !Path.IsPathFullyQualified(exe) || !Path.IsPathFullyQualified(evidence)) throw new ArgumentException("Both --exe and --evidence must be absolute paths.");
            exe = Path.GetFullPath(exe); evidence = Path.GetFullPath(evidence);
            if (!File.Exists(exe)) throw new FileNotFoundException("Desktop executable was not found.", exe);
            var data = Path.Combine(evidence, Guid.NewGuid().ToString("N"), "data");
            return new(exe, data, evidence);
        }
    }
}
