using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LanShare.Desktop.Services;

/// <summary>右键菜单的安装形态。</summary>
public enum ContextMenuMode
{
    /// <summary>未安装。</summary>
    None,

    /// <summary>已安装：HKCU 注册表 + IExplorerCommand 组件。</summary>
    Installed
}

/// <summary>安装结果：形态 + 给用户看的说明。</summary>
public readonly record struct ContextMenuInstallResult(ContextMenuMode Mode, string Message);

/// <summary>
/// 资源管理器右键菜单。
/// 实现方式是纯用户级注册表（HKCU）：三个 <c>shell\LanShare</c> 动词键指向一个
/// <c>ExplorerCommandHandler</c> CLSID，CLSID 再在 HKCU 里指向随程序分发的
/// <c>LanShare.Shell.dll</c>（实现 IExplorerCommand）。
/// 不需要 MSIX 包、不需要管理员、不需要代码签名证书。
/// Windows 11 会把 IExplorerCommand 直接显示在一级右键菜单里。
/// </summary>
public static class ContextMenuInstaller
{
    private const string MenuText = "用内网快传分享";
    private const string CommandClsid = "{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}";
    private const string ShellDllName = "LanShare.Shell.dll";

    /// <summary>三个动词键（文件 / 文件夹 / 文件夹空白处）。</summary>
    private static readonly string[] VerbKeys =
    {
        @"Software\Classes\*\shell\LanShare",
        @"Software\Classes\Directory\shell\LanShare",
        @"Software\Classes\Directory\Background\shell\LanShare"
    };

    private static string ClsidKey => @"Software\Classes\CLSID\" + CommandClsid;
    private static string InprocKey => ClsidKey + @"\InprocServer32";

    /// <summary>当前实际生效的菜单形态。</summary>
    public static ContextMenuMode Detect() => IsInstalled() ? ContextMenuMode.Installed : ContextMenuMode.None;

    public static bool IsInstalled()
    {
        try
        {
            using (var inproc = Registry.CurrentUser.OpenSubKey(InprocKey, false))
            {
                var dll = inproc?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) return false;
            }

            foreach (var path in VerbKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, false);
                if (key is null) return false;
                if (!string.Equals(key.GetValue("ExplorerCommandHandler") as string, CommandClsid,
                        StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ContextMenuInstaller.IsInstalled", ex);
            return false;
        }
    }

    public static string Describe(ContextMenuMode mode) => mode switch
    {
        ContextMenuMode.Installed => "已安装：在资源管理器里右键文件或文件夹即可分享。",
        _ => "未安装：安装后可在资源管理器右键分享。"
    };

    /// <summary>安装菜单：写 3 个动词键 + 1 个 CLSID 键，然后通知资源管理器刷新。</summary>
    public static ContextMenuInstallResult Install()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dll = Path.Combine(appDir, ShellDllName);
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                "程序目录缺少 " + ShellDllName + "，无法安装右键菜单。请使用完整发布包。", dll);
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(appDir, "LanShare.exe");

        // 直接写注册表；被杀软/组策略拦下时退回到 reg.exe（系统签名程序，通常被放行）。
        try
        {
            WriteWithRegistryApi(exe, dll);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write("ContextMenuInstaller.Install fallback to reg.exe", ex);
            WriteWithRegExe(exe, dll);
        }

        ShellRefresh.NotifyAssociationsChanged();

        return new ContextMenuInstallResult(ContextMenuMode.Installed, Describe(ContextMenuMode.Installed));
    }

    private static void WriteWithRegistryApi(string exe, string dll)
    {
        foreach (var path in VerbKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true)
                            ?? throw new InvalidOperationException("无法写入右键菜单注册表：" + path);

            key.SetValue("MUIVerb", MenuText, RegistryValueKind.String);
            key.SetValue("Icon", exe + ",0", RegistryValueKind.String);
            key.SetValue("ExplorerCommandHandler", CommandClsid, RegistryValueKind.String);
            key.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);

            // 经典 verb 形态的 command 子键会让菜单退化成固定命令行（且无法多选传参），必须确保不存在。
            try { key.DeleteSubKeyTree("command", false); } catch { }
        }

        using (var clsid = Registry.CurrentUser.CreateSubKey(ClsidKey, true)
                           ?? throw new InvalidOperationException("无法写入 CLSID 注册表：" + ClsidKey))
        {
            clsid.SetValue(null, "LanShare Explorer Command", RegistryValueKind.String);
        }

        using var inproc = Registry.CurrentUser.CreateSubKey(InprocKey, true)
                           ?? throw new InvalidOperationException("无法写入 CLSID 注册表：" + InprocKey);
        inproc.SetValue(null, dll, RegistryValueKind.String);
        inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
    }

    /// <summary>用 reg.exe 写入同样的键；失败时抛出带 stderr 的异常。</summary>
    private static void WriteWithRegExe(string exe, string dll)
    {
        var args = new List<string>();
        foreach (var path in VerbKeys)
        {
            var key = @"HKCU" + path;
            args.AddRange(new[] { "add", key, "/f", "/v", "MUIVerb", "/t", "REG_SZ", "/d", MenuText });
            args.AddRange(new[] { "add", key, "/f", "/v", "Icon", "/t", "REG_SZ", "/d", exe + ",0" });
            args.AddRange(new[] { "add", key, "/f", "/v", "ExplorerCommandHandler", "/t", "REG_SZ", "/d", CommandClsid });
            args.AddRange(new[] { "add", key, "/f", "/v", "MultiSelectModel", "/t", "REG_SZ", "/d", "Player" });
        }

        var clsidKey = @"HKCU" + ClsidKey;
        var inprocKey = @"HKCU" + InprocKey;
        args.AddRange(new[] { "add", clsidKey, "/f", "/ve", "/t", "REG_SZ", "/d", "LanShare Explorer Command" });
        args.AddRange(new[] { "add", inprocKey, "/f", "/ve", "/t", "REG_SZ", "/d", dll });
        args.AddRange(new[] { "add", inprocKey, "/f", "/v", "ThreadingModel", "/t", "REG_SZ", "/d", "Apartment" });

        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动 reg.exe 来写入右键菜单注册表。");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new UnauthorizedAccessException(
                "写入右键菜单注册表被系统拒绝（reg.exe 退出码 " + process.ExitCode + "）：" +
                (string.IsNullOrWhiteSpace(stderr) ? "无更多信息。" : stderr.Trim()));
        }
    }

    /// <summary>卸载菜单：清掉动词键与 CLSID，并通知资源管理器刷新。</summary>
    public static void Uninstall()
    {
        var failures = new List<string>();

        foreach (var path in VerbKeys)
        {
            if (!TryDeleteKey(path, out var error))
            {
                failures.Add(error!);
            }
        }

        if (!TryDeleteKey(ClsidKey, out var clsidError))
        {
            failures.Add(clsidError!);
        }

        ShellRefresh.NotifyAssociationsChanged();

        if (failures.Count > 0)
        {
            throw new UnauthorizedAccessException(string.Join("；", failures));
        }
    }

    /// <summary>删除一个 HKCU 键；API 被拒时改用 reg.exe。返回 false 表示两条路都失败。</summary>
    private static bool TryDeleteKey(string path, out string? error)
    {
        error = null;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write("ContextMenuInstaller.DeleteSubKeyTree fallback " + path, ex);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ContextMenuInstaller.DeleteSubKeyTree " + path, ex);
            return true; // 其它错误（例如键不存在）不算失败。
        }

        try
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in new[] { "delete", @"HKCU" + path, "/f" }) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) { error = "无法启动 reg.exe。"; return false; }
            var stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // 键本来就不存在时 reg.exe 返回 1，这属于成功。
            if (process.ExitCode != 0 && !stderr.Contains("unable to find", StringComparison.OrdinalIgnoreCase))
            {
                error = "删除 " + path + " 失败：" + stderr.Trim();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "删除 " + path + " 失败：" + ex.Message;
            return false;
        }
    }
}

/// <summary>通知资源管理器重新读取右键菜单。</summary>
internal static class ShellRefresh
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfFlush = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void NotifyAssociationsChanged()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { ErrorLog.Write("ShellRefresh.NotifyAssociationsChanged", ex); }
    }
}
