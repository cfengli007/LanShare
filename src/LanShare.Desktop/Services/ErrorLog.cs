using System.IO;
using LanShare.Core;

namespace LanShare.Desktop.Services;

public static class ErrorLog
{
    public static string LogPath => Path.Combine(LanShareConstants.AppDataDirectory, "desktop-errors.log");

    public static void Write(string context, Exception? exception)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {context}: {exception}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
