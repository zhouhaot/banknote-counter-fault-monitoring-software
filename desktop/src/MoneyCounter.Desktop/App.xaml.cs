using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoneyCounter.Core.Registry;
using MoneyCounter.Core.Operations;
using MoneyCounter.Infrastructure.Operations;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Infrastructure.Maintenance;
using MoneyCounter.Infrastructure.Storage;
using MoneyCounter.Desktop.Infrastructure;
using MoneyCounter.Desktop.ViewModels;

namespace MoneyCounter.Desktop;
public partial class App : Application
{
    private ServiceProvider? _services;
    private SingleInstance? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoneyCounterMonitor", "data");
            if (e.Args.Length > 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-dir" || !Path.IsPathFullyQualified(e.Args[1])) throw new ArgumentException("启动参数仅支持 --data-dir 加绝对目录。");
                dataDir = Path.GetFullPath(e.Args[1]);
            }
            Directory.CreateDirectory(dataDir);
            _instance = new SingleInstance(dataDir);
            if (!_instance.IsPrimary) { _instance.SignalPrimary(); Shutdown(); return; }
            var services = new ServiceCollection();
            var logDir = Path.Combine(dataDir, "..", "logs");
            Directory.CreateDirectory(logDir);
            services.AddLogging(b => b.AddProvider(new FileLoggerProvider(Path.Combine(logDir, "app.log"))));
            services.AddSingleton(new DbStore(Path.Combine(dataDir, "app.sqlite3")));
            services.AddSingleton<IRegistryService, SqliteRegistryService>();
            services.AddSingleton<IOperationsService, SqliteOperationsService>();
            services.AddSingleton<IMaintenanceService, SqliteMaintenanceService>();
            services.AddSingleton<RegistryViewModel>();
            _services = services.BuildServiceProvider();
            await _services.GetRequiredService<DbStore>().InitializeAsync();
            var vm = _services.GetRequiredService<RegistryViewModel>();
            var configDir = Path.Combine(dataDir, "..", "config");
            Directory.CreateDirectory(configDir);
            var window = new MainWindow(vm, _services.GetRequiredService<ILogger<RegistryEditor>>(), Path.Combine(configDir, "window-placement.json"),
                d => new OperationsViewModel(d, _services.GetRequiredService<IOperationsService>(), _services.GetRequiredService<ILogger<OperationsViewModel>>()),
                (d, a) => new MaintenanceWindow(d, _services.GetRequiredService<IMaintenanceService>(), _services.GetRequiredService<ILogger<MaintenanceViewModel>>(), a)); MainWindow = window;
            _instance.StartActivationListener(window); window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            await vm.RefreshAsync();
        }
        catch (Exception ex)
        {
            _services?.GetService<ILogger<App>>()?.LogError(ex, "Startup failed");
            MessageBox.Show("程序未能启动，原有数据不会被自动重建。请检查数据目录权限或日志。\n" + ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally { _instance?.Dispose(); base.OnExit(e); }
    }
}
