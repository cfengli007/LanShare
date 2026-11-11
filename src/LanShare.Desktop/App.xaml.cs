using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LanShare.Core;
using LanShare.Core.Infrastructure;
using LanShare.Desktop.Services;

namespace LanShare.Desktop;

public partial class App : Application
{
    private SingleInstanceManager? _singleInstance;

    public bool IsExiting { get; private set; }
    public MainWindow? ShellWindow => MainWindow as MainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 必须最先设：界面里的日期/数字/排序都按中文习惯走。
        // DefaultThreadCurrentCulture 对之后创建的线程也生效（托盘线程、HTTP 线程都会继承）。
        try
        {
            var zh = new CultureInfo("zh-CN");
            CultureInfo.DefaultThreadCurrentCulture = zh;
            CultureInfo.DefaultThreadCurrentUICulture = zh;
            CultureInfo.CurrentCulture = zh;
            CultureInfo.CurrentUICulture = zh;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SetCulture", ex);
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
        SessionEnding += (_, _) => EmergencyShutdown();

        var sharePaths = ParseSharePaths(e.Args);

        // 命令行维护入口：供安装包、脚本与排查使用（不启动界面）。
        if (TryRunMaintenanceCommand(e.Args)) return;
        var initialPage = ParsePage(e.Args);

        _singleInstance = new SingleInstanceManager();
        if (!_singleInstance.TryAcquirePrimary())
        {
            // 调用发生在 UI 线程；ForwardAsync 内部 await 会捕获 Dispatcher 上下文。
            // 必须放到线程池上等待，否则 UI 线程阻塞会让续体永远无法执行（死锁）。
            var forwarded = Task.Run(() => _singleInstance.ForwardAsync(sharePaths, sharePaths.Count == 0))
                .GetAwaiter().GetResult();
            if (!forwarded)
            {
                MessageBox.Show(
                    "内网快传已经在运行，但无法连接到主实例。请先在系统托盘中退出旧实例再试。",
                    "内网快传", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        _singleInstance.StartServer(OnPipeRequest);

        // 建窗口之前先把主题定下来，否则会先闪一下默认深色再跳到用户设的浅色。
        // 此时 AppServices 还没初始化（异步），所以直接读 settings.json 的 Theme 字段。
        ApplyStartupTheme();

        var window = new MainWindow(sharePaths, initialPage);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 启动早期直接读 settings.json 里的 Theme，把主题定下来。
    ///
    /// 为什么不直接用 AppServices.SettingsSnapshot：那条路要 await InitializeAsync（起 Kestrel、
    /// 建存储目录），在这里同步等待会拖慢首屏；而且窗口 Show 之前主题就该定好，否则会闪一下。
    /// 读失败一律回退深色，绝不影响启动。
    /// </summary>
    private static void ApplyStartupTheme()
    {
        var theme = ThemeService.Dark;
        try
        {
            var settingsPath = Path.Combine(LanShareConstants.AppDataDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonFile.ReadOptions);
                if (!string.IsNullOrWhiteSpace(loaded?.Theme)) theme = loaded!.Theme;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ApplyStartupTheme", ex);
        }

        try { ThemeService.Apply(theme); }
        catch (Exception ex) { ErrorLog.Write("ApplyStartupTheme.Apply", ex); }
    }

    /// <summary>处理 --install-context-menu / --uninstall-context-menu / --check-context-menu。</summary>
    private bool TryRunMaintenanceCommand(string[] args)
    {
        var command = args
            .FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)
                                 && a.EndsWith("-context-menu", StringComparison.OrdinalIgnoreCase));
        if (command is null) return false;

        var log = Path.Combine(Path.GetTempPath(), "lanshare-context-menu.log");
        var exitCode = 0;
        try
        {
            switch (command.ToLowerInvariant())
            {
                case "--install-context-menu":
                    var result = ContextMenuInstaller.Install();
                    File.WriteAllText(log, "install: " + result.Message);
                    break;
                case "--uninstall-context-menu":
                    ContextMenuInstaller.Uninstall();
                    File.WriteAllText(log, "uninstall: ok");
                    break;
                default:
                    var installed = ContextMenuInstaller.IsInstalled();
                    File.WriteAllText(log, "check: " + installed);
                    if (!installed) exitCode = 2;
                    break;
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            try { File.WriteAllText(log, "error: " + ex); } catch { }
            ErrorLog.Write("MaintenanceCommand " + command, ex);
        }

        Shutdown(exitCode);
        return true;
    }

    private void OnPipeRequest(PipeRequest request)
    {
        if (request.Paths.Count > 0)
        {
            AppServices.Instance.EnqueueShareRequest(request.Paths);
        }

        Dispatcher.BeginInvoke(() => ShellWindow?.ShowFromExternalRequest());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("DispatcherUnhandledException", e.Exception);
        try
        {
            AppServices.Instance.ReportError("出现未处理的错误", e.Exception.Message);
        }
        catch
        {
            MessageBox.Show(e.Exception.Message, "内网快传", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        ErrorLog.Write("Exit", null);

        try { ShellWindow?.PrepareForExit(); } catch { }
        try { ThemeService.Shutdown(); } catch { }
        try { await AppServices.Instance.ShutdownAsync(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        Shutdown();
    }

    private void EmergencyShutdown()
    {
        IsExiting = true;
        try { ShellWindow?.PrepareForExit(); } catch { }
        try { ThemeService.Shutdown(); } catch { }
        try { AppServices.Instance.ShutdownAsync().Wait(TimeSpan.FromSeconds(4)); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsExiting)
        {
            try { AppServices.Instance.ShutdownAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
        try { ThemeService.Shutdown(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static string? ParsePage(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                return arg["--page=".Length..].Trim();
        }
        return null;
    }

    private static List<string> ParseSharePaths(string[] args)
    {
        var result = new List<string>();
        var shareMode = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--share", StringComparison.OrdinalIgnoreCase))
            {
                shareMode = true;
                continue;
            }

            if (arg.StartsWith("--share=", StringComparison.OrdinalIgnoreCase))
            {
                shareMode = true;
                AddPath(result, arg["--share=".Length..]);
                continue;
            }

            if (shareMode && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                AddPath(result, arg);
            }
        }
        return result;
    }

    private static void AddPath(List<string> target, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var value = raw.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value)) return;
        if (target.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        target.Add(value);
    }
}