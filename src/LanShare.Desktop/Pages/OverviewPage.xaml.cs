using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LanShare.Core;
using LanShare.Desktop.Models;
using LanShare.Desktop.Services;

namespace LanShare.Desktop.Pages;

/// <summary>
/// 概览页（ShellPage 体系，WPF-UI 已移除）。
///
/// 三块内容：服务状态 / 访问地址 / 最近分享。
/// 坑记：
///   • 根元素是 UserControl，页面不自带背景，背景由 MainWindow 内容区提供。
///   • 页面内提示条是本页自绘的 Border（OkSoft/WarnSoft/DangerSoft 三套配色），
///     不依赖任何尚未交付的公共组件。
///   • 状态一律颜色 + 文字双编码：徽标底色换了，徽标文字也会跟着换。
///   • 底部主操作由 BuildActions() 交给 MainWindow 的按钮栏，页头不再重复放按钮。
///   • 存储目录探针必须后台跑：目录在网络盘或权限受限时会阻塞，写 UI 线程上会卡界面。
/// </summary>
public partial class OverviewPage : ShellPage
{
    private readonly ObservableCollection<ShareRow> _recent = new();
    private bool _refreshing;
    private bool _storageCheckRunning;
    private bool _storageProbeDeferred;
    private bool _storageWarningShown;

    public OverviewPage()
    {
        InitializeComponent();
        RecentItems.ItemsSource = _recent;

        AppServices.Instance.StateChanged += OnStateChanged;
        AppServices.Instance.SharesChanged += OnSharesChanged;
        Loaded += OnLoaded;
    }

    // ===================== ShellPage 生命周期 =====================

    public override void ActivatePage()
    {
        UpdateStatus();
        _ = CheckStorageWritableAsync();
    }

    public override void RefreshPage()
    {
        _ = RefreshAsync();
        _ = CheckStorageWritableAsync();
    }

    /// <summary>底部按钮栏主操作：打开网页 + 新建分享。</summary>
    public override FrameworkElement? BuildActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var openWeb = new Button
        {
            Content = "打开分享网页",
            Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        openWeb.Click += OpenWeb_Click;

        var newShare = new Button
        {
            Content = "新建分享",
            Style = (Style)FindResource("FlatButton")
        };
        newShare.Click += NewShare_Click;

        panel.Children.Add(openWeb);
        panel.Children.Add(newShare);
        return panel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        await RefreshAsync();
        await CheckStorageWritableAsync();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateStatus();
        _ = RefreshAsync();
    }

    private void OnSharesChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    // ===================== 服务状态 =====================

    private void UpdateStatus()
    {
        var services = AppServices.Instance;
        try
        {
            InternalUrlBox.Text = services.InternalBase;
            ExternalUrlBox.Text = services.ExternalBase;
            ListenText.Text = services.IsRuntimeAvailable
                ? $"{services.SettingsSnapshot.BindAddress}:{services.SettingsSnapshot.Port}"
                : "—";

            if (!services.IsRuntimeAvailable)
            {
                ApplyBanner("Danger", "服务未启动",
                    string.IsNullOrWhiteSpace(services.StartupError)
                        ? "后端服务尚未就绪，请稍后重试。"
                        : services.StartupError!);
                ApplyBadge("Danger", "未启动");
                RetryButton.Visibility = Visibility.Visible;
            }
            else if (services.IsServerRunning)
            {
                ApplyBanner("Ok", "服务运行中", services.ServerDetailText);
                ApplyBadge("Ok", "运行中");
                RetryButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ApplyBanner("Warn", "服务已停止", services.ServerDetailText);
                ApplyBadge("Warn", "已停止");
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Overview status", ex);
        }
    }

    /// <summary>状态徽标：颜色 + 文字同时换，避免只靠颜色编码状态。</summary>
    private void ApplyBadge(string severity, string text)
    {
        StatusBadge.Style = (Style)FindResource(severity switch
        {
            "Ok" => "BadgeOk",
            "Warn" => "BadgeWarn",
            "Danger" => "BadgeDanger",
            _ => "BadgeAccent"
        });
        StatusBadgeText.Style = (Style)FindResource(severity switch
        {
            "Ok" => "BadgeTextOk",
            "Warn" => "BadgeTextWarn",
            "Danger" => "BadgeTextDanger",
            _ => "BadgeTextAccent"
        });
        StatusBadgeText.Text = text;
    }

    /// <summary>页面内提示条：左侧 3px 色条 + 标题 + 正文。</summary>
    private void ApplyBanner(string severity, string title, string message)
    {
        var (soft, strong) = severity switch
        {
            "Ok" => ("OkSoft", "Ok"),
            "Warn" => ("WarnSoft", "Warn"),
            "Danger" => ("DangerSoft", "Danger"),
            _ => ("AccentSoft", "Accent")
        };

        ServiceBanner.Background = (System.Windows.Media.Brush)FindResource(soft);
        ServiceBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(strong);
        ServiceBannerTitle.Foreground = (System.Windows.Media.Brush)FindResource(strong);
        ServiceBannerTitle.Text = title;
        ServiceBannerMessage.Text = message;
    }

    // ===================== 存储目录可写性探针 =====================

    /// <summary>
    /// 探测存储目录是否真的可写。
    ///
    /// 为什么需要它：storageDirectory 指向无写权限的目录时，网页端上传会在写 .tmp 那一步
    /// 抛 UnauthorizedAccessException 直接 500，而桌面端界面毫无提示，用户完全无从判断。
    ///
    /// 实现要点：
    ///   • 真写一个文件再删掉 —— 只看 Directory.Exists 或 ACL 都不准（网络盘、只读挂载、
    ///     继承权限受限都会骗过静态判断）。
    ///   • 整个 IO 放 Task.Run：目录在离线网络盘上会卡很久，绝不能占 UI 线程。
    ///   • 目录不存在同样算不可写（上传时一样会失败），文案区分开更好定位。
    /// </summary>
    private async Task CheckStorageWritableAsync()
    {
        if (_storageCheckRunning) return;

        // 运行时还没初始化完时不要探。
        // 为什么：AppServices.DataDirectory 在 Runtime == null 时回落到 LanShareConstants.AppDataDirectory
        // （即 %LOCALAPPDATA%\LanShare），而真正的数据目录可能是 LANSHARE_DATA_DIR 指定的别处。
        // 早探会拿错目录，报出一条假的"存储目录不可写"，而实际上传一切正常（踩过这个误报）。
        // 正确做法：等 Runtime 就绪后再探，这里挂一次 StateChanged 兜底。
        if (AppServices.Instance.Runtime is null)
        {
            if (!_storageProbeDeferred)
            {
                _storageProbeDeferred = true;
                EventHandler? onStateChanged = null;
                onStateChanged = (_, _) =>
                {
                    if (AppServices.Instance.Runtime is null) return;
                    AppServices.Instance.StateChanged -= onStateChanged;
                    _ = CheckStorageWritableAsync();
                };
                AppServices.Instance.StateChanged += onStateChanged;
            }
            return;
        }

        _storageCheckRunning = true;

        try
        {
            // 必须用和服务端**完全相同**的解析逻辑，否则探针会探一个目录、服务写另一个目录，
            // 界面提示与实际行为不一致。
            var directory = SettingsService.ResolveStorageDirectory(
                AppServices.Instance.SettingsSnapshot,
                AppServices.Instance.DataDirectory);

            var probe = await Task.Run(() => ProbeStorage(directory));

            // Task.Run 的续体回到 UI 线程（await 捕获了上下文），可以安全碰控件。
            ApplyStorageWarning(probe.Directory, probe.Problem);
        }
        catch (Exception ex)
        {
            // 探针自身出错不能让页面挂掉，也不能误报成"目录不可写"。
            ErrorLog.Write("Overview storage probe", ex);
        }
        finally
        {
            _storageCheckRunning = false;
        }
    }

    private readonly record struct StorageProbeResult(string Directory, string? Problem);

    /// <summary>在后台线程执行：写一个随机探针文件再删掉。</summary>
    private static StorageProbeResult ProbeStorage(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new StorageProbeResult("(未设置)", "存储目录未配置。");

        try
        {
            if (!Directory.Exists(directory))
            {
                // 目录不存在：可能是还没建，也可能是盘符/网络盘不可达。
                // 对上传而言两者都一样是失败，所以都报出来。
                return new StorageProbeResult(directory, "存储目录不存在或无法访问。");
            }

            var probePath = Path.Combine(directory, ".lswrite-" + Path.GetRandomFileName()[..6]);
            try
            {
                File.WriteAllText(probePath, "LanShare storage probe");
            }
            finally
            {
                // 写完立刻删；删除失败也不能留着垃圾文件。
                try { if (File.Exists(probePath)) File.Delete(probePath); }
                catch { /* 清不掉就算了，不能因为清理失败影响结论 */ }
            }

            return new StorageProbeResult(directory, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageProbeResult(directory, "当前用户没有写入权限。");
        }
        catch (DirectoryNotFoundException)
        {
            return new StorageProbeResult(directory, "存储目录不存在或无法访问。");
        }
        catch (IOException ex)
        {
            // 磁盘满、介质只读、网络盘断开都落到这里。
            return new StorageProbeResult(directory, ex.Message);
        }
        catch (Exception ex)
        {
            // 非法路径字符、路径过长等：同样意味着写不进去。
            return new StorageProbeResult(directory, ex.Message);
        }
    }

    /// <summary>回 UI 线程后更新警告条。可写则隐藏。</summary>
    private void ApplyStorageWarning(string directory, string? problem)
    {
        // 续体可能比页面生命周期活得久（快速来回切页），控件已释放就别碰。
        if (StorageWarning is null) return;

        if (problem is null)
        {
            StorageWarning.Visibility = Visibility.Collapsed;
            if (_storageWarningShown)
            {
                _storageWarningShown = false;
                Toast("存储目录已恢复", "现在可以正常上传文件了。", isSuccess: true);
            }
            return;
        }

        StorageWarningTitle.Text = "存储目录不可写：" + directory;
        StorageWarningMessage.Text = problem + "网页上传会失败，请在「设置 → 存储」里换一个目录。";
        StorageWarning.Visibility = Visibility.Visible;

        if (!_storageWarningShown)
        {
            _storageWarningShown = true;
            SetStatus("存储目录不可写，网页上传会失败");
        }
    }

    // ===================== 最近分享 =====================

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var services = AppServices.Instance;
            var shares = await services.LoadSharesAsync();
            _recent.Clear();
            foreach (var record in shares.Take(6))
            {
                _recent.Add(new ShareRow(record, services.InternalShareUrl(record), services.ExternalShareUrl(record)));
            }

            var isEmpty = _recent.Count == 0;
            EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            RecentItems.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            SetStatus(isEmpty ? "就绪" : $"{_recent.Count} 条分享");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Overview refresh", ex);
            SetStatus("加载分享失败：" + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ===================== 命令 =====================

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        SetStatus("正在重试启动服务…");
        try
        {
            await AppServices.Instance.RetryInitializeAsync();
            Toast("服务", "已尝试重新启动服务。");
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("重试失败", ex.Message);
        }
        finally
        {
            RetryButton.IsEnabled = true;
            UpdateStatus();
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        SetStatus("正在重启服务…");
        try
        {
            await AppServices.Instance.RestartServerAsync();
            Toast("服务", "服务已重启。", isSuccess: true);
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("重启服务失败", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
            UpdateStatus();
        }
    }

    private void NewShare_Click(object sender, RoutedEventArgs e) =>
        AppServices.Instance.RequestNavigation(typeof(NewSharePage));

    private void ViewAll_Click(object sender, RoutedEventArgs e) =>
        AppServices.Instance.RequestNavigation(typeof(SharesPage));

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        Toast("已刷新", "分享列表已更新。");
    }

    private async void CopyInternal_Click(object sender, RoutedEventArgs e) =>
        await ShellHelper.CopyTextAsync(AppServices.Instance.InternalBase, "已复制内网地址", AppServices.Instance.InternalBase);

    private async void CopyExternal_Click(object sender, RoutedEventArgs e) =>
        await ShellHelper.CopyTextAsync(AppServices.Instance.ExternalBase, "已复制外网地址", AppServices.Instance.ExternalBase);

    private void OpenInternal_Click(object sender, RoutedEventArgs e) =>
        ShellHelper.OpenUrl(AppServices.Instance.InternalBase);

    private void OpenExternal_Click(object sender, RoutedEventArgs e) =>
        ShellHelper.OpenUrl(AppServices.Instance.ExternalBase);

    private void OpenWeb_Click(object sender, RoutedEventArgs e) =>
        ShellHelper.OpenUrl(AppServices.Instance.WebUrl);

    private async void CopyRecent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;
        await ShellHelper.CopyTextAsync(row.InternalUrl, "已复制分享链接", row.InternalUrl);
    }

    private void OpenRecent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;
        ShellHelper.OpenUrl(row.InternalUrl);
    }
}
