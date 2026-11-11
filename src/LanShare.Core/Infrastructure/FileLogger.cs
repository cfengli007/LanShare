using System.Text;

namespace LanShare.Core.Infrastructure;

/// <summary>Small, dependency-free, thread-safe file logger writing %DATA%\logs\server-YYYYMMDD.log.</summary>
public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly int _retentionDays;
    private StreamWriter? _writer;
    private DateTime _writerDate = DateTime.MinValue;
    private DateTime _lastCleanupDate = DateTime.MinValue;
    private bool _disposed;

    public FileLogger(string directory, string filePrefix = "server", int retentionDays = 7)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("日志目录不能为空。", nameof(directory));
        _directory = Path.GetFullPath(directory);
        _filePrefix = SafePaths.SanitizeFileName(filePrefix, "server");
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
    }

    public string Directory => _directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var now = DateTime.Now;
                var writer = GetWriterLocked(now);
                if (writer == null) return;
                writer.Write('[');
                writer.Write(now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                writer.Write("] [");
                writer.Write(level);
                writer.Write("] ");
                writer.Write(message);
                if (exception != null)
                {
                    writer.WriteLine();
                    writer.Write(exception);
                }
                writer.WriteLine();
            }
            catch
            {
                // Logging must never take the application down.
            }
        }
    }

    private StreamWriter? GetWriterLocked(DateTime now)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var date = now.Date;
        if (_writer != null && _writerDate == date) return _writer;

        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;

        var path = Path.Combine(_directory, $"{_filePrefix}-{date:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _writerDate = date;

        if (_lastCleanupDate != date)
        {
            _lastCleanupDate = date;
            CleanupOldFilesLocked(date);
        }

        return _writer;
    }

    private void CleanupOldFilesLocked(DateTime today)
    {
        try
        {
            var cutoff = today.AddDays(-_retentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"{_filePrefix}-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // Another process may hold the file; try again tomorrow.
                }
            }
        }
        catch
        {
            // Retention cleanup must never break logging.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
    }
}
