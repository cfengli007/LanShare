using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LanShare.Desktop.Services;
using LanShare.Desktop.Services.Update;

namespace LanShare.Desktop.Dialogs;

/// <summary>
/// 检查更新窗口。骨架：自绘标题栏 40 / 内容 ScrollViewer / 按钮栏 64。
///
/// 固定公共 API（MainWindow 菜单「立即检查更新」直接调）：
///     public static Task ShowAsync(Window? owner, bool autoStart)
///
/// 数据来源：
///   • 检查 —— UpdateService.Instance.CheckAsync(ct)（会顺带更新 Settings.LastCheck）
///   • 下载 —— UpdateService.Instance.DownloadAsync(result, progress, ct)
///   • 安装 —— UpdateService.Instance.LaunchInstaller(path)（它自己会 Application.Shutdown()）
///
/// 状态机由 _phase 驱动，ActionButton 复用同一个按钮完成
/// 「检查 → 下载并安装 → 运行安装程序」三段，避免按钮换来换去。
///
/// 坑：
///   • LaunchInstaller 成功后进程会退出，之后不要再碰任何控件；
///     用户取消 UAC 会抛 Win32Exception(1223)，要当成正常取消而不是错误弹窗。
///   • 下载进度回调可能在线程池线程上，必须用 Dispatcher 回 UI 线程。
/// </summary>
public partial class AppUpdateWindow : Window
{
    private enum Phase
    {
        Idle,       // 还没检查
        Checking,   // 检查中
        UpToDate,   // 已是最新
        Available,  // 有新版本，可下载
        Downloading,
        ReadyToRun, // 已下载完，按钮变「运行安装程序」
        Failed
    }

    private readonly bool _autoStart;
    private Phase _phase = Phase.Idle;
    private UpdateCheckResult? _result;
    private string? _downloadedFile;
    private CancellationTokenSource? _cts;

    public AppUpdateWindow(bool autoStart)
    {
        InitializeComponent();

        _autoStart = autoStart;

        CurrentVersionText.Text = SelfUpdater.CurrentVersion;
        RepoText.Text = SelfUpdater.RepoUrl;

        SourceInitialized += (_, _) => ThemeService.AttachWindow(this);
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>
    /// 显示检查更新窗口并等待它关闭。
    /// autoStart=true 表示打开就立刻检查一次（菜单「立即检查更新」用）；
    /// false 则停在「尚未检查」状态等用户点按钮。
    /// </summary>
    public static Task ShowAsync(Window? owner, bool autoStart)
    {
        var w = new AppUpdateWindow(autoStart);
        if (owner is not null && owner.IsVisible) w.Owner = owner;

        // ShowDialog 自带消息泵，会阻塞到这里窗口关闭为止。
        // 签名保留 Task 是为了让调用方可以 await（菜单处理器里写起来更自然）。
        w.ShowDialog();
        return Task.CompletedTask;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective);

        if (_autoStart)
        {
            await CheckAsync();
        }
        else
        {
            SetPhase(Phase.Idle);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    // ---------------------------------------------------------------- 状态机

    /// <summary>只改按钮文案与可交互性，真正的文案由各 Show* 方法设置。</summary>
    private void SetPhase(Phase phase)
    {
        _phase = phase;
        ActionButton.IsEnabled = phase is not Phase.Checking and not Phase.Downloading;

        ActionButton.Content = phase switch
        {
            Phase.Idle => "检查更新",
            Phase.Checking => "检查中…",
            Phase.UpToDate => "重新检查",
            Phase.Available => _result?.Installer is not null ? "下载并安装" : "打开发布页",
            Phase.Downloading => "正在下载…",
            Phase.ReadyToRun => "运行安装程序",
            Phase.Failed => "重试",
            _ => "检查更新"
        };

        Bar.Visibility = phase is Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowChecking()
    {
        StatusTitle.Text = "正在检查…";
        StatusDetail.Text = "连接 GitHub Releases";
        LatestVersionText.Text = "—";
        ReleaseNotes.Text = "（检查后显示）";
        HintText.Text = "";
        Bar.Value = 0;
        SetPhase(Phase.Checking);
    }

    public void ShowUpdate(UpdateCheckResult result)
    {
        LatestVersionText.Text = string.IsNullOrWhiteSpace(result.LatestVersion) ? "—" : result.LatestVersion;

        var release = result.Release;
        if (release is not null)
        {
            StatusDetail.Text = release.PublishedAt is { } publishedAt
                ? $"发布于 {publishedAt:yyyy-MM-dd HH:mm}"
                : (string.IsNullOrWhiteSpace(release.Name) ? "有可用更新" : release.Name);

            var body = SelfUpdater.ToPlainText(release.Body);
            ReleaseNotes.Text = string.IsNullOrWhiteSpace(body) ? "（这个版本没有写更新说明）" : body;
        }
        else
        {
            StatusDetail.Text = "有可用更新";
            ReleaseNotes.Text = "（这个版本没有写更新说明）";
        }

        StatusTitle.Text = $"发现新版本 {result.LatestVersion}";
        HintText.Text = "下载后会提示你运行安装程序";

        SetPhase(Phase.Available);
        ActionButton.IsEnabled = true;
    }

    public void ShowUpToDate(UpdateCheckResult result)
    {
        LatestVersionText.Text = string.IsNullOrWhiteSpace(result.LatestVersion)
            ? result.CurrentVersion
            : result.LatestVersion;

        StatusTitle.Text = "已是最新版本";
        StatusDetail.Text = $"当前 {result.CurrentVersion} 就是最新版本，无需更新。";
        ReleaseNotes.Text = "（没有新版本）";
        HintText.Text = "";

        SetPhase(Phase.UpToDate);
    }

    public void ShowError(string message, string? hint = null)
    {
        StatusTitle.Text = "检查失败";
        StatusDetail.Text = message;
        HintText.Text = hint ?? "可稍后重试，或到项目主页手动下载";
        SetPhase(Phase.Failed);
        ActionButton.IsEnabled = true;
    }

    /// <summary>下载进度。回调可能来自线程池，必须切回 UI 线程。</summary>
    public void ShowDownloadProgress(long done, long total)
    {
        var pct = total > 0 ? done * 100.0 / total : 0;
        Bar.Value = Math.Clamp(pct, 0, 100);

        StatusTitle.Text = "正在下载安装包";
        StatusDetail.Text = total > 0
            ? $"{FormatMb(done)} / {FormatMb(total)} MB（{pct:F0}%）"
            : $"{FormatMb(done)} MB";
    }

    private static double FormatMb(long bytes) => bytes / 1048576.0;

    // ---------------------------------------------------------------- 动作

    private async Task CheckAsync()
    {
        ShowChecking();

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        UpdateCheckResult result;
        try
        {
            result = await UpdateService.Instance.CheckAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowError("检查已取消");
            return;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AppUpdateWindow.CheckAsync", ex);
            ShowError(ex.Message);
            return;
        }

        _result = result;

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            ShowError(result.Error);
            return;
        }

        if (result.HasUpdate) ShowUpdate(result);
        else ShowUpToDate(result);
    }

    private async Task DownloadAsync()
    {
        if (_result is null) return;

        if (_result.Installer is null)
        {
            // 没有安装包资产：退回到发布页，让用户手动下载。
            SelfUpdater.OpenUrl(_result.Release?.HtmlUrl ?? SelfUpdater.RepoUrl + "/releases");
            return;
        }

        StatusTitle.Text = "正在下载安装包";
        StatusDetail.Text = _result.Installer.Name;
        HintText.Text = _result.Installer.Name;
        Bar.Value = 0;
        SetPhase(Phase.Downloading);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var progress = new Progress<(long done, long total)>(p =>
        {
            if (!Dispatcher.HasShutdownStarted) ShowDownloadProgress(p.done, p.total);
        });

        try
        {
            _downloadedFile = await UpdateService.Instance.DownloadAsync(_result, progress, _cts.Token);

            StatusTitle.Text = "下载完成";
            StatusDetail.Text = "点击「运行安装程序」开始升级";
            HintText.Text = _downloadedFile;
            Bar.Value = 100;
            SetPhase(Phase.ReadyToRun);
        }
        catch (OperationCanceledException)
        {
            StatusTitle.Text = "下载已取消";
            StatusDetail.Text = "可以重新点「下载并安装」再试一次。";
            SetPhase(Phase.Available);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AppUpdateWindow.DownloadAsync", ex);
            StatusTitle.Text = "下载失败";
            StatusDetail.Text = ex.Message;
            HintText.Text = "可稍后重试，或到项目主页手动下载";
            SetPhase(Phase.Available);
        }
    }

    private void RunInstaller()
    {
        if (string.IsNullOrWhiteSpace(_downloadedFile)) return;

        var confirmed = MsgWindow.Confirm(this, "安装更新",
            "安装包已下载：\n" + _downloadedFile + "\n\n" +
            "点击「确定」将启动安装程序（会请求管理员权限），\n" +
            "它会自动关闭正在运行的 LanShare。");
        if (!confirmed) return;

        try
        {
            // 成功后 UpdateService 会直接 Application.Shutdown()，这里不要再碰控件。
            UpdateService.Instance.LaunchInstaller(_downloadedFile);
        }
        catch (Exception ex) when (UpdateService.IsUserCancelledElevation(ex))
        {
            HintText.Text = "已取消管理员授权，安装未开始";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AppUpdateWindow.RunInstaller", ex);
            StatusTitle.Text = "启动安装程序失败";
            StatusDetail.Text = ex.Message;
            HintText.Text = "可手动打开：" + _downloadedFile;
        }
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Checking:
            case Phase.Downloading:
                return;

            case Phase.ReadyToRun:
                RunInstaller();
                return;

            case Phase.Available:
                await DownloadAsync();
                return;

            case Phase.UpToDate:
            case Phase.Failed:
            case Phase.Idle:
            default:
                await CheckAsync();
                return;
        }
    }

    // ---------------------------------------------------------------- 杂项

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject src && IsInButton(src)) return;
        try { DragMove(); } catch { /* 拖动期间窗口可能已被关闭 */ }
    }

    private static bool IsInButton(DependencyObject? node)
    {
        while (node is not null and not Window)
        {
            if (node is System.Windows.Controls.Button) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnOpenRepo(object sender, MouseButtonEventArgs e) =>
        SelfUpdater.OpenUrl(_result?.Release?.HtmlUrl ?? SelfUpdater.RepoUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
