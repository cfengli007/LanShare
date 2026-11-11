using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace LanShare.Desktop.Services;

public static class ShellHelper
{
    public static async Task<bool> CopyTextAsync(string text, string successTitle = "已复制", string successMessage = "内容已复制到剪贴板。")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppServices.Instance.ReportError("复制失败", "没有可复制的内容。");
            return false;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                AppServices.Instance.ShowSuccess(successTitle, successMessage);
                return true;
            }
            catch (COMException)
            {
                await Task.Delay(60);
            }
            catch (Exception ex)
            {
                AppServices.Instance.ReportError("复制失败", ex.Message);
                return false;
            }
        }

        AppServices.Instance.ReportError("复制失败", "剪贴板正被其他程序占用，请稍后重试。");
        return false;
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AppServices.Instance.ReportError("无法打开", "地址为空。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("无法打开链接", ex.Message);
        }
    }

    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
        {
            AppServices.Instance.ReportError("无法打开目录", "目录不存在：" + path);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("无法打开目录", ex.Message);
        }
    }

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppServices.Instance.ReportError("无法定位", "路径为空。");
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                AppServices.Instance.ReportError("无法定位", "文件或目录已不存在：" + path);
            }
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("无法打开资源管理器", ex.Message);
        }
    }

    public static void OpenNetworkConnections()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("无法打开网络连接", ex.Message);
        }
    }
}
