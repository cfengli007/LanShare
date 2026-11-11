using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LanShare.Desktop.Services;

public readonly record struct ElevationResult(bool Success, string? Error);

/// <summary>生成临时 PowerShell 脚本并以管理员身份（UAC）运行，用于 netsh 等需要提权的操作。</summary>
public static class ElevationHelper
{
    public static async Task<ElevationResult> RunElevatedAsync(string operationName, string scriptBody)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LanShare");
        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(folder, $"elevate-{id}.ps1");
        var logPath = Path.Combine(folder, $"elevate-{id}.log");

        var script = BuildScript(scriptBody, logPath);
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true));

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return new ElevationResult(false, "无法启动管理员进程。");

            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                return new ElevationResult(true, null);

            var detail = TryReadLog(logPath);
            return new ElevationResult(false, string.IsNullOrWhiteSpace(detail)
                ? $"{operationName}未完成（退出码 {process.ExitCode}）。"
                : detail.Trim());
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevationResult(false, "已取消管理员授权，未做任何修改。");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ElevationHelper " + operationName, ex);
            return new ElevationResult(false, ex.Message);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(logPath); } catch { }
        }
    }

    private static string BuildScript(string body, string logPath)
    {
        var escapedLog = logPath.Replace("'", "''");
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("$log = '" + escapedLog + "'");
        builder.AppendLine("try {");
        builder.AppendLine("  $output = & {");
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            builder.AppendLine("    " + line);
        }
        builder.AppendLine("  } 2>&1");
        builder.AppendLine("  $code = $LASTEXITCODE");
        builder.AppendLine("  ($output | Out-String) | Out-File -FilePath $log -Encoding utf8");
        builder.AppendLine("  exit $code");
        builder.AppendLine("} catch {");
        builder.AppendLine("  ($_ | Out-String) | Out-File -FilePath $log -Encoding utf8");
        builder.AppendLine("  exit 1");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string? TryReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
