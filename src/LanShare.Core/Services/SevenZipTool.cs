using System.Diagnostics;
using System.Text;
using LanShare.Core.Infrastructure;

namespace LanShare.Core;

/// <summary>Raised when 7-Zip is missing or returns a non-zero exit code.</summary>
public sealed class SevenZipException : Exception
{
    public int ExitCode { get; }

    public SevenZipException(string message, int exitCode, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
    }
}

/// <summary>Thin async wrapper around 7za.exe; resolves the executable from the app folder, Program Files or PATH.</summary>
public sealed class SevenZipTool
{
    private readonly object _gate = new();
    private readonly FileLogger? _log;
    private readonly string? _explicitPath;
    private string? _resolved;

    public SevenZipTool(string? executablePath = null, FileLogger? logger = null)
    {
        _explicitPath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath.Trim().Trim('"');
        _log = logger;
    }

    public string? ExecutablePath
    {
        get
        {
            lock (_gate)
            {
                return _resolved ??= ResolveExecutable();
            }
        }
    }

    public bool IsAvailable => ExecutablePath != null;

    public void Refresh()
    {
        lock (_gate) _resolved = null;
    }

    public string EnsureAvailable()
    {
        var path = ExecutablePath;
        if (path == null)
        {
            throw new InvalidOperationException(
                "未找到 7-Zip 程序（7za.exe / 7z.exe）。请将 7za.exe 放到程序目录的 Tools 文件夹，或安装 7-Zip 后重试。");
        }

        return path;
    }

    private string? ResolveExecutable()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                _log?.Info($"已找到 7-Zip：{candidate}");
                return candidate;
            }
            catch
            {
                // Invalid PATH entries are ignored.
            }
        }

        _log?.Error("未找到 7-Zip 可执行文件（Tools\\7za.exe、C:\\Program Files\\7-Zip\\7z.exe 或 PATH）。");
        return null;
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("LANSHARE_7Z_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment.Trim().Trim('"');

        if (_explicitPath != null) yield return _explicitPath;

        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "Tools", "7za.exe");
        yield return Path.Combine(baseDirectory, "Tools", "7z.exe");
        yield return Path.Combine(baseDirectory, "7za.exe");
        yield return Path.Combine(baseDirectory, "7z.exe");

        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\7-Zip\7z.exe";
            yield return @"C:\Program Files\7-Zip\7za.exe";
        }

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string trimmed;
            try { trimmed = directory.Trim().Trim('"'); } catch { continue; }
            if (trimmed.Length == 0) continue;
            foreach (var exeName in new[] { "7za.exe", "7z.exe" })
            {
                string candidate;
                try { candidate = Path.Combine(trimmed, exeName); }
                catch { continue; }
                yield return candidate;
            }
        }
    }

    /// <summary>Adds files/directories (names relative to <paramref name="workingDirectory"/>) to a .7z archive.</summary>
    public async Task AddAsync(string archivePath, string workingDirectory, IReadOnlyList<string> inputNames, CancellationToken ct = default)
    {
        if (inputNames == null || inputNames.Count == 0)
            throw new ArgumentException("没有要打包的文件。", nameof(inputNames));

        var executable = EnsureAvailable();
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"打包工作目录不存在：{workingDirectory}");

        var archiveFullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(archiveFullPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-t7z");
        // 仅存储（Copy），不做压缩：多文件/大文件的目的是合成单一下载文件，压缩只会拖慢创建速度。
        // 仍生成标准 .7z，Windows 11 资源管理器、7-Zip、WinRAR 等均可直接解压。
        startInfo.ArgumentList.Add("-mx=0");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-bd");
        startInfo.ArgumentList.Add("-bso1");
        startInfo.ArgumentList.Add("-sccUTF-8");
        startInfo.ArgumentList.Add(archiveFullPath);
        startInfo.ArgumentList.Add("--");
        foreach (var name in inputNames) startInfo.ArgumentList.Add(name);

        _log?.Info($"7-Zip 打包：{archiveFullPath}（{inputNames.Count} 项）");
        await RunAsync(startInfo, archiveFullPath, expectedArchive: true, ct).ConfigureAwait(false);
    }

    /// <summary>Extracts an archive into <paramref name="outputDirectory"/> (used by tests and diagnostics).</summary>
    public async Task ExtractAsync(string archivePath, string outputDirectory, CancellationToken ct = default)
    {
        var executable = EnsureAvailable();
        var archiveFullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(archiveFullPath)) throw new FileNotFoundException("压缩包不存在。", archiveFullPath);

        var outputFullPath = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputFullPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = outputFullPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-bd");
        startInfo.ArgumentList.Add("-bso1");
        startInfo.ArgumentList.Add("-sccUTF-8");
        startInfo.ArgumentList.Add(archiveFullPath);
        startInfo.ArgumentList.Add($"-o{outputFullPath}");

        _log?.Info($"7-Zip 解压：{archiveFullPath} -> {outputFullPath}");
        await RunAsync(startInfo, archivePath: null, expectedArchive: false, ct).ConfigureAwait(false);
    }

    private async Task RunAsync(ProcessStartInfo startInfo, string? archivePath, bool expectedArchive, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 7-Zip 进程。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法启动 7-Zip：{ex.Message}", ex);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = BuildDetail(stdout, stderr);
                if (expectedArchive && archivePath != null) SafePaths.TryDeleteFile(archivePath, _log);
                _log?.Error($"7-Zip 失败（退出码 {process.ExitCode}）：{detail}");
                throw new SevenZipException(
                    $"7-Zip 执行失败（退出码 {process.ExitCode}）。{detail}", process.ExitCode);
            }

            if (expectedArchive && archivePath != null)
            {
                var info = new FileInfo(archivePath);
                if (!info.Exists || info.Length == 0)
                    throw new SevenZipException("7-Zip 未生成有效的压缩包。", process.ExitCode);
            }
        }
    }

    private static string BuildDetail(string stdout, string stderr)
    {
        var text = (stderr + " " + stdout).Trim();
        if (text.Length == 0) return "";
        text = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }
}
