using System.Text;

namespace MediaCleanup.Service;

/// <summary>Writes one plain-text log file per day and deletes files older than two weeks.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;
    private readonly string _directory;
    private readonly object _gate = new();
    private DateTime _lastCleanup = DateTime.MinValue;

    public FileLoggerProvider(string directory) => _directory = directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this);

    public void Dispose() { }

    private void Write(LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append(" [").Append(Label(level)).Append("] ")
            .Append(message);
        if (exception != null) line.AppendLine().Append(exception);
        line.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                try
                {
                    File.AppendAllText(Path.Combine(_directory, $"mediacleanup-{now:yyyyMMdd}.log"), line.ToString(), Encoding.UTF8);
                }
                catch (UnauthorizedAccessException)
                {
                    // Today's file was created by another account (for example before the service's
                    // log-on account was changed), so write to a file of our own instead.
                    File.AppendAllText(Path.Combine(_directory, $"mediacleanup-{now:yyyyMMdd}-{Environment.UserName}.log"), line.ToString(), Encoding.UTF8);
                }
                if (now - _lastCleanup > TimeSpan.FromHours(12))
                {
                    _lastCleanup = now;
                    foreach (var old in Directory.EnumerateFiles(_directory, "mediacleanup-*.log"))
                    {
                        if (File.GetLastWriteTime(old) < now.AddDays(-KeepDays)) File.Delete(old);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the service down.
            }
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        LogLevel.Debug or LogLevel.Trace => "DEBUG",
        _ => "INFO",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;

        public FileLogger(FileLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, formatter(state, exception), exception);
        }
    }
}
