using System.Text;

namespace LanShare.Core.Infrastructure;

/// <summary>File-system helpers that keep user supplied names and paths inside known locations.</summary>
public static class SafePaths
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Strips directory components, invalid characters and Windows trailing dots/spaces.
    /// Never returns an empty string; the result is safe to use as a single path segment.</summary>
    public static string SanitizeFileName(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var value = name.Replace('\\', '/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\0' || ch < ' ') continue;
            if (Array.IndexOf(InvalidFileNameChars, ch) >= 0) continue;
            builder.Append(ch);
        }

        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result is "." or "..") return fallback;

        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem)) result = "_" + result;

        const int maxLength = 120;
        if (result.Length > maxLength)
        {
            var extension = Path.GetExtension(result);
            if (extension.Length > 20) extension = "";
            var stemMax = Math.Max(1, maxLength - extension.Length);
            var stemPart = Path.GetFileNameWithoutExtension(result);
            if (stemPart.Length > stemMax) stemPart = stemPart[..stemMax];
            result = stemPart + extension;
        }

        result = result.Trim().TrimEnd('.', ' ');
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Returns a name unique within <paramref name="used"/>, appending " (2)", " (3)" ... on collision.</summary>
    public static string MakeUnique(string desired, ISet<string> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        var candidate = string.IsNullOrWhiteSpace(desired) ? "file" : desired;
        if (used.Add(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var i = 2; i < 10_000; i++)
        {
            var next = $"{stem} ({i}){extension}";
            if (used.Add(next)) return next;
        }

        var fallback = $"{stem}-{Guid.NewGuid():N}{extension}";
        used.Add(fallback);
        return fallback;
    }

    /// <summary>True when <paramref name="path"/> is a descendant of <paramref name="root"/> (not equal to it).</summary>
    public static bool IsWithin(string root, string path)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pathFull = Path.GetFullPath(path);
            if (rootFull.Length == 0) return false;
            var prefix = rootFull + Path.DirectorySeparatorChar;
            return pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the path is a drive/filesystem root and therefore must never be deleted recursively.</summary>
    public static bool IsRootPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length > 0 && string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Size of a file or directory (recursive). Unreadable entries are skipped with a log warning.</summary>
    public static long GetPathSize(string path, FileLogger? log = null)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in EnumerateFilesRecursive(path, log))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) { log?.Warn($"读取文件大小失败：{file}：{ex.Message}"); }
            }
            return total;
        }
        catch (Exception ex)
        {
            log?.Warn($"计算路径大小失败：{path}：{ex.Message}");
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string directory, FileLogger? log)
    {
        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (Exception ex)
        {
            log?.Warn($"读取目录失败：{directory}：{ex.Message}");
            files = Array.Empty<string>();
        }

        foreach (var file in files) yield return file;

        string[] directories;
        try { directories = Directory.GetDirectories(directory); }
        catch (Exception ex)
        {
            log?.Warn($"读取子目录失败：{directory}：{ex.Message}");
            directories = Array.Empty<string>();
        }

        foreach (var child in directories)
        {
            foreach (var file in EnumerateFilesRecursive(child, log)) yield return file;
        }
    }

    public static bool TryDeleteFile(string? path, FileLogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (IsRootPath(full)) return false;
            if (!File.Exists(full)) return false;
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"删除文件失败：{path}：{ex.Message}");
            return false;
        }
    }

    public static bool TryDeleteDirectory(string? path, FileLogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (IsRootPath(full)) return false;
            if (!Directory.Exists(full)) return false;
            Directory.Delete(full, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"删除目录失败：{path}：{ex.Message}");
            return false;
        }
    }
}
