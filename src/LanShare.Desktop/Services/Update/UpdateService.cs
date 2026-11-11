using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LanShare.Desktop.Services.Update;

/// <summary>
/// 更新服务：把 <see cref="SelfUpdater"/>（纯 HTTP）和 <see cref="UpdateSettings"/>（持久化）
/// 串起来，负责自动检查节流、手动检查、下载、启动安装器。
///
/// 设计要点：
///   • 单例 <see cref="Instance"/>。UI 层（MainWindow / AppUpdateWindow）只跟它打交道。
///   • 自动检查是「静默」的：发现新版本只触发 <see cref="UpdateFound"/> 事件
///     （MainWindow 据此点亮 UpdateBadge），绝不弹窗打断用户。
///   • 手动检查才弹 AppUpdateWindow，由调用方决定，服务本身不认识任何窗口。
///   • 后台检查的一切异常都在这里吞掉并写 ErrorLog —— update.json 坏了、
///     断网、GitHub 抽风，都不允许把主程序搞崩。
/// </summary>
public sealed class UpdateService
{
    private static readonly Lazy<UpdateService> LazyInstance = new(() => new UpdateService());

    public static UpdateService Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _autoCts;
    private bool _checking;
    private DateTime _startedAt = DateTime.Now;

    private UpdateService()
    {
        Settings = UpdateSettings.Load();
    }

    /// <summary>当前设置。UI 改完后调用 <see cref="SaveSettings"/> 或直接 Settings.Save()。</summary>
    public UpdateSettings Settings { get; private set; }

    /// <summary>自动检查发现新版本时触发（UI 线程）。参数是检查结果。</summary>
    public event Action<UpdateCheckResult>? UpdateFound;

    /// <summary>自动检查完成（无论有无更新）时触发（UI 线程），便于点亮/熄灭徽标。</summary>
    public event Action<UpdateCheckResult>? AutoCheckCompleted;

    /// <summary>重新从磁盘载入设置（例如外部改了 update.json）。</summary>
    public void ReloadSettings()
    {
        Settings = UpdateSettings.Load();
    }

    /// <summary>保存设置并让自动检查按新间隔重新排期。</summary>
    public bool SaveSettings()
    {
        var ok = Settings.Save();
        if (!ok) ErrorLog.Write("UpdateSettings.Save", new IOException(Settings.SaveError ?? "unknown"));
        RescheduleIfRunning();
        return ok;
    }

    /// <summary>
    /// 手动/自动共用的检查入口。
    /// 注意：不受 Settings.Enabled 影响 —— Enabled 只管自动检查；
    /// 用户手动点「检查更新」时必须真的发请求。
    /// 网络异常不抛，包进 <see cref="UpdateCheckResult.Error"/>。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        UpdateCheckResult result;
        try
        {
            result = await SelfUpdater.CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult { CurrentVersion = SelfUpdater.CurrentVersion, Error = "检查已取消" };
        }
        catch (Exception ex)
        {
            ErrorLog.Write("UpdateService.CheckAsync", ex);
            return new UpdateCheckResult { CurrentVersion = SelfUpdater.CurrentVersion, Error = ex.Message };
        }

        if (result.Error is null)
        {
            Settings.LastCheck = DateTime.Now;
            SaveSettings();
        }

        return result;
    }

    /// <summary>
    /// 启动自动检查：启动后 8 秒首次（让主窗口先画完），之后按 IntervalHours 轮询。
    /// 重复调用是安全的（会先停掉旧的）。
    /// 不会保存 owner 引用 —— 服务不认识窗口，避免持有 WPF 对象导致退出时泄漏。
    /// </summary>
    public void StartAutoCheck(Window? owner = null)
    {
        _ = owner; // 保留参数是为了调用方语义清晰；本服务不持有窗口引用。

        lock (_gate)
        {
            StopAutoCheckCore();
            _startedAt = DateTime.Now;

            if (!Settings.Enabled) return;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.IntervalHours * 60)),
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        // 首次检查推迟 8 秒，避免和启动时的服务初始化抢 CPU / 网络。
        var cts = new CancellationTokenSource();
        lock (_gate) { _autoCts = cts; }
        _ = FirstCheckAfterDelayAsync(cts.Token);
    }

    public void StopAutoCheck()
    {
        lock (_gate) { StopAutoCheckCore(); }
    }

    private void StopAutoCheckCore()
    {
        try { _timer?.Stop(); } catch { }
        if (_timer is not null)
        {
            try { _timer.Tick -= OnTimerTick; } catch { }
            _timer = null;
        }

        try { _autoCts?.Cancel(); } catch { }
        try { _autoCts?.Dispose(); } catch { }
        _autoCts = null;
    }

    private void RescheduleIfRunning()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            if (!Settings.Enabled)
            {
                StopAutoCheckCore();
                return;
            }
            _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.IntervalHours * 60));
        }
    }

    private async Task FirstCheckAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunAutoCheckAsync(ct).ConfigureAwait(false);
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            // 刚启动不足一个间隔时不要立刻再查一次（首次检查已经覆盖过了）。
            var elapsed = DateTime.Now - _startedAt;
            if (elapsed < TimeSpan.FromMinutes(Settings.IntervalHours * 60))
                return;

            var ct = _autoCts?.Token ?? CancellationToken.None;
            await RunAutoCheckAsync(ct).ConfigureAwait(false);
            _startedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程，必须兜住。
            ErrorLog.Write("UpdateService.OnTimerTick", ex);
        }
    }

    /// <summary>
    /// 跑一次静默检查。任何失败都只记日志。
    /// 命中 SkippedVersion 时也走这里 —— 结果照常回报，由订阅方决定要不要点亮徽标。
    /// </summary>
    private async Task RunAutoCheckAsync(CancellationToken ct)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (!Settings.Enabled) return;

            // 未到间隔就先不查（例如用户频繁重启程序）。
            if (Settings.LastCheck is { } last)
            {
                var since = DateTime.Now - last;
                if (since < TimeSpan.FromHours(Settings.IntervalHours)) return;
            }

            var result = await SelfUpdater.CheckAsync(ct).ConfigureAwait(false);

            if (result.Error is not null)
            {
                ErrorLog.Write("UpdateService.AutoCheck", new InvalidOperationException(result.Error));
                return;
            }

            Settings.LastCheck = DateTime.Now;
            Settings.Save();

            // 用户明确跳过过的版本，自动检查不再打扰。
            var skipped = Settings.SkippedVersion;
            var relevant = result.HasUpdate &&
                           (string.IsNullOrEmpty(skipped) ||
                            SelfUpdater.CompareVersion(result.LatestVersion, skipped!) != 0);

            OnUi(() =>
            {
                AutoCheckCompleted?.Invoke(result);
                if (relevant) UpdateFound?.Invoke(result);
            });
        }
        catch (OperationCanceledException)
        {
            // 退出时被取消，正常路径。
        }
        catch (Exception ex)
        {
            ErrorLog.Write("UpdateService.RunAutoCheck", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>记录用户「跳过此版本」的选择。</summary>
    public void SkipVersion(string version)
    {
        Settings.SkippedVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        SaveSettings();
    }

    /// <summary>
    /// 下载安装包（委托给 SelfUpdater），返回落盘绝对路径。
    /// r.Installer 为空时抛 InvalidOperationException —— 调用方应捕获并提示。
    /// </summary>
    public Task<string> DownloadAsync(
        UpdateCheckResult r, IProgress<(long done, long total)>? progress, CancellationToken ct = default)
    {
        if (r is null) throw new ArgumentNullException(nameof(r));
        var asset = r.Installer
                    ?? throw new InvalidOperationException("这个版本没有可下载的安装包，请到发布页手动下载。");
        return SelfUpdater.DownloadInstallerAsync(asset, progress, ct);
    }

    /// <summary>
    /// 启动安装程序并退出本程序。
    ///
    /// UseShellExecute=true + Verb="runas"：安装包本身要求管理员权限，
    /// 这里显式 runas 让 UAC 弹在我们的前台进程上，而不是等 UAC 自己判断。
    /// 启动成功后调用方应关闭窗口；这里直接 Application.Shutdown()。
    /// 启动失败（用户点了 UAC 取消 → Win32Exception 1223）会抛出来，让调用方提示。
    /// </summary>
    public void LaunchInstaller(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("安装包路径为空。", nameof(exePath));
        if (!File.Exists(exePath)) throw new FileNotFoundException("安装包不存在，请重新下载。", exePath);

        // 只有 .exe 才可能是安装包。SelfUpdater.SelectInstaller 在「没有 exe」时会兜底返回 .zip，
        // 那时直接 Process.Start(zip) 只是让资源管理器把它当压缩包打开，装不了东西。
        // 这里显式挡住，给用户一句能看懂的话，而不是莫名其妙打开一个压缩包。
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "这个版本只提供了压缩包，没有可自动运行的安装程序，请在发布页手动下载。");
        }

        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Path.GetTempPath(),
        });

        try
        {
            StopAutoCheck();
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("UpdateService.LaunchInstaller.Shutdown", ex);
        }
    }

    /// <summary>判断是不是「用户取消了 UAC」，UI 可以据此显示得更友好。</summary>
    public static bool IsUserCancelledElevation(Exception ex) =>
        ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223;

    internal static void OnUi(Action action)
    {
        if (action is null) return;
        var app = Application.Current;
        if (app is null)
        {
            action();
            return;
        }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
