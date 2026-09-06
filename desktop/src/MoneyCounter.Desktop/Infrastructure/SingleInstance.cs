using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
namespace MoneyCounter.Desktop.Infrastructure;
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly FileStream? _dataDirectoryLock;
    private readonly bool _ownsMutex;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;
    public bool IsPrimary { get; }
    public SingleInstance(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDirectory.ToUpperInvariant())))[..24];
        _mutex = new Mutex(true, "Local\\MoneyCounter." + key, out var created);
        _ownsMutex = created;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MoneyCounter.Activate." + key);
        if (created)
        {
            try
            {
                _dataDirectoryLock = new FileStream(Path.Combine(canonicalDirectory, ".moneycounter.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                IsPrimary = true;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                // A process from another Windows session already owns this data directory.
                IsPrimary = false;
            }
        }
    }
    public void SignalPrimary()
    {
        if (_dataDirectoryLock is null && _ownsMutex)
        {
            MessageBox.Show("此数据目录已在另一 Windows 会话中打开。请切换到该会话后继续使用。", "程序已打开", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _activate.Set();
    }
    public void StartActivationListener(Window window) => _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        window.Dispatcher.BeginInvoke(() => { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Show(); window.Activate(); }), null, Timeout.Infinite, false);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Unregister(null);
        _activate.Dispose();
        _dataDirectoryLock?.Dispose();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
