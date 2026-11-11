using LanShare.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace LanShare.Server.Hosting;

/// <summary>Bridges Microsoft.Extensions.Logging into the small file logger (warnings and errors only).</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogger _fileLogger;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(FileLogger fileLogger, LogLevel minimumLevel = LogLevel.Warning)
    {
        _fileLogger = fileLogger;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new Bridge(_fileLogger, categoryName, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class Bridge : ILogger
    {
        private readonly FileLogger _fileLogger;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public Bridge(FileLogger fileLogger, string category, LogLevel minimumLevel)
        {
            _fileLogger = fileLogger;
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message;
            try { message = formatter(state, exception); }
            catch { message = state?.ToString() ?? ""; }

            var category = _category.Length > 60 ? _category[..60] : _category;
            var line = $"[{logLevel}] {category}: {message}";
            if (logLevel >= LogLevel.Error) _fileLogger.Error(line, exception);
            else if (logLevel == LogLevel.Warning) _fileLogger.Warn(line);
            else _fileLogger.Info(line);
        }
    }
}
