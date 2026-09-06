using Microsoft.Extensions.Logging;
using System.IO;

namespace MoneyCounter.Desktop.Infrastructure;

internal sealed class FileLoggerProvider(string logPath) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(logPath, categoryName, _gate);
    public void Dispose() { }

    private sealed class FileLogger(string path, string category, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = $"{DateTimeOffset.Now:O} [{level}] {category}: {formatter(state, exception)}{(exception is null ? "" : " [" + exception.GetType().Name + "]")}{Environment.NewLine}";
            lock (gate)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length >= 5 * 1024 * 1024)
                    {
                        for (var i = 4; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                        File.Move(path, path + ".1", true);
                    }
                    File.AppendAllText(path, line);
                }
                catch (IOException ex) { System.Diagnostics.Trace.TraceError("Application log unavailable: " + ex.GetType().Name); }
                catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.TraceError("Application log unavailable: " + ex.GetType().Name); }
            }
        }
    }
}
