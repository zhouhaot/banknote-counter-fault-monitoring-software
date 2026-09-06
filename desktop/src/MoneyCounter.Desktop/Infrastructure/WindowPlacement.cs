using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace MoneyCounter.Desktop.Infrastructure;

internal sealed class WindowPlacement(string path)
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public void Restore(Window window)
    {
        var saved = Read();
        if (saved is null) return;

        window.Width = Math.Max(window.MinWidth, saved.Width);
        window.Height = Math.Max(window.MinHeight, saved.Height);
        window.Left = saved.Left;
        window.Top = saved.Top;
    }

    public void EnsureVisible(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return;

        var monitor = MonitorFromRect(ref bounds, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var maxLeft = Math.Max(info.WorkArea.Left, info.WorkArea.Right - width);
        var maxTop = Math.Max(info.WorkArea.Top, info.WorkArea.Bottom - height);
        var left = Math.Clamp(bounds.Left, info.WorkArea.Left, maxLeft);
        var top = Math.Clamp(bounds.Top, info.WorkArea.Top, maxTop);
        if (left != bounds.Left || top != bounds.Top)
            SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public void Save(Window window)
    {
        try
        {
            var bounds = window.RestoreBounds;
            if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinite(bounds.Width) || !IsFinite(bounds.Height)) return;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Snapshot(bounds.Left, bounds.Top, bounds.Width, bounds.Height)));
            File.Move(temporaryPath, path, true);
        }
        catch (IOException ex) { System.Diagnostics.Trace.TraceError("Window placement save failed: {0}", ex.GetType().Name); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.TraceError("Window placement save failed: {0}", ex.GetType().Name); }
    }

    private Snapshot? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            return saved is not null && IsFinite(saved.Left) && IsFinite(saved.Top) && saved.Width > 0 && saved.Height > 0 ? saved : null;
        }
        catch (IOException ex) { System.Diagnostics.Trace.TraceError("Window placement read failed: {0}", ex.GetType().Name); return null; }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.TraceError("Window placement read failed: {0}", ex.GetType().Name); return null; }
        catch (JsonException ex) { System.Diagnostics.Trace.TraceError("Window placement read failed: {0}", ex.GetType().Name); return null; }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed record Snapshot(double Left, double Top, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect WorkArea; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
