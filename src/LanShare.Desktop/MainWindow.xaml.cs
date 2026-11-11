using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LanShare.Core;
using LanShare.Desktop.Controls;
using LanShare.Desktop.Pages;
using LanShare.Desktop.Services;
using LanShare.Desktop.Services.Update;

namespace LanShare.Desktop;

/// <summary>
/// 主窗口逻辑（自绘骨架版，已彻底移除 WPF-UI）。
///
/// 结构对应 MainWindow.xaml 的五行：标题栏 / 菜单栏 / 内容区 / 状态条 / 按钮栏。
/// 这里负责三件事：
///   1. 导航：把 ShellPage 实例塞进 PageHost，并把页面的 BuildActions() 塞进按钮栏左侧
///   2. 反馈：SetStatus / Toast / Busy / Unbusy —— 页面只调这四个，不自己弹窗
///   3. 壳层设施：托盘、单实例、关闭到托盘、菜单命令
///
/// 三个必须记住的坑：
///   • 标题栏拖动必须排除按钮（IsInButton），否则 DragMove 抢走鼠标捕获，按钮永远不出 Click
///   • Busy 遮罩只挂在 BusyLayer（内容区那一格），绝不替换 Window.Content、绝不设 IsEnabled=false
///   • Toast 走 Controls/ToastHost，不再自己画
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>项目主页（与 SettingsPage / AboutWindow 保持一致）。</summary>
    private const string ProjectUrl = "https://github.com/cfengli007/LanShare";

    private readonly IReadOnlyList<string> _initialSharePaths;
    private readonly Type? _initialPage;
    private ShellPage? _currentPage;
    private Type? _currentPageType;
    private IDisposable? _busy;
    private bool _loaded;
    private bool _allowClose;
    private bool _trayHintShown;
    private bool _trayAvailable;

    public MainWindow(IReadOnlyList<string>? initialSharePaths = null, string? initialPage = null)
    {
        InitializeComponent();
        _initialSharePaths = initialSharePaths ?? Array.Empty<string>();
        _initialPage = ResolvePage(initialPage);

        AppServices.Instance.StateChanged += OnStateChanged;
        AppServices.Instance.SnackbarRequested += OnSnackbarRequested;
        AppServices.Instance.AutoShareStarting += OnAutoShareStarting;
        AppServices.Instance.AutoShareCreated += OnAutoShareCreated;
        AppServices.Instance.ShareCreatedNotification += OnShareCreatedNotification;
        AppServices.Instance.NavigationRequested += OnNavigationRequested;

        UpdateTrayState();
        SyncUpdateMenu();

        // 先在构造函数里把尺寸夹进工作区。
        // 这一步必须早于 Show()：WindowStartupLocation=CenterScreen 是在 Show 时
        // 按当前 Width/Height 算居中位置的，尺寸先夹好，位置才会算对。
        ClampSizeToWorkArea();
    }

    /// <summary>
    /// HWND 已经建好但窗口还没画出来，是唯一能"改尺寸/位置而用户看不见跳动"的时机。
    /// Window_Loaded 太晚——那时窗口已经按错误尺寸显示过了。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ClampPositionToWorkArea();
        try { ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective); } catch { }
    }

    // ==================== 窗口生命周期 ====================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        // 新窗口 Show 之前先挂主题（DWM 深色框架只能在有 HWND 后设，以 SourceInitialized 兜底）
        try { ThemeService.AttachWindow(this); } catch { }

        TrySetTrayIcon();
        NavigateTo(typeof(OverviewPage));
        UpdateTrayState();

        try
        {
            await AppServices.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("InitializeAsync", ex);
            AppServices.Instance.ReportError("启动失败", ex.Message);
        }

        UpdateTrayState();
        ApplyCurrentTheme();
        StartAutoUpdateCheck();

        if (_initialPage is not null)
            NavigateTo(_initialPage);

        if (_initialSharePaths.Count > 0)
        {
            AppServices.Instance.EnqueueShareRequest(_initialSharePaths);
        }

        SetStatus("就绪");
    }

    /// <summary>
    /// 把窗口尺寸夹进工作区（不含任务栏的那块区域）。
    ///
    /// 为什么必须做：本机 1920x1200 / 150% 缩放，窗口 1180x760 DIP 实际占 1770x1140 像素，
    /// CenterScreen 居中后底边会超过工作区底边（任务栏约 48px），
    /// 底部 62px 的按钮栏被切掉，用户看不到「打开分享网页 / 新建分享 / 检查更新」。
    ///
    /// 单位说明：SystemParameters.WorkArea 是 DIP，与 Width/Height/Left/Top 同单位，可直接比较。
    /// 时机说明：必须在 Show() 之前调（这里放在构造函数末尾），
    /// 因为 CenterScreen 的居中位置是按 Show 那一刻的 Width/Height 算出来的。
    /// </summary>
    private void ClampSizeToWorkArea()
    {
        try
        {
            var (w, h) = FitSizeToArea(Width, Height, MinWidth, MinHeight, SystemParameters.WorkArea);
            Width = w;
            Height = h;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ClampSizeToWorkArea", ex);
        }
    }

    /// <summary>
    /// 尺寸夹取的纯函数版本（不碰实例状态，便于单测）。
    /// 工作区比窗口小时，把窗口缩到工作区减 16（留一点边距给阴影/圆角），但不小于 MinWidth/MinHeight。
    /// </summary>
    public static (double Width, double Height) FitSizeToArea(
        double width, double height, double minWidth, double minHeight, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return (width, height);

        var w = width > area.Width ? Math.Max(minWidth, area.Width - 16) : width;
        var h = height > area.Height ? Math.Max(minHeight, area.Height - 16) : height;
        return (w, h);
    }

    /// <summary>
    /// 把窗口位置夹进工作区。此时 HWND 已建好、窗口还没画，改位置用户看不见跳动。
    /// 未 Show 时 Left/Top 是 NaN（CenterScreen 还没算），所以必须先判 NaN。
    /// 最大化/最小化时不要动，交给系统。
    /// </summary>
    private void ClampPositionToWorkArea()
    {
        try
        {
            if (WindowState != WindowState.Normal) return;

            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : Height;

            var left = double.IsNaN(Left) ? double.NaN : Left;
            var top = double.IsNaN(Top) ? double.NaN : Top;

            var (nl, nt) = FitPositionToArea(left, top, w, h, SystemParameters.WorkArea);
            Left = nl;
            Top = nt;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ClampPositionToWorkArea", ex);
        }
    }

    /// <summary>
    /// 位置夹取的纯函数版本。left/top 为 NaN 时按工作区居中（等价 CenterScreen）。
    /// 然后保证整个窗口矩形落在 area 内。
    /// </summary>
    public static (double Left, double Top) FitPositionToArea(
        double left, double top, double width, double height, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return (left, top);
        if (width <= 0 || height <= 0) return (left, top);

        var l = double.IsNaN(left) ? area.Left + (area.Width - width) / 2 : left;
        var t = double.IsNaN(top) ? area.Top + (area.Height - height) / 2 : top;

        if (l < area.Left) l = area.Left;
        else if (l + width > area.Right) l = Math.Max(area.Left, area.Right - width);

        if (t < area.Top) t = area.Top;
        else if (t + height > area.Bottom) t = Math.Max(area.Top, area.Bottom - height);

        return (l, t);
    }

    private void ApplyCurrentTheme()
    {
        try
        {
            ThemeService.Apply(AppServices.Instance.SettingsSnapshot.Theme);
            ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ApplyCurrentTheme", ex);
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaxIcon is null) return;
        // 最大化后图标换成"还原"图形，避免按钮和窗口状态对不上
        MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M 0 3 L 7 3 L 7 10 L 0 10 Z M 3 3 L 3 0 L 10 0 L 10 7 L 7 7"
            : "M 0 0 L 10 0 L 10 10 L 0 10 Z");
        BtnMax.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    // ==================== 标题栏 ====================

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮上时不拖动 —— 否则 DragMove() 会抢走鼠标捕获，按钮永远收不到 Click
        if (IsInButton(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;

        // 最大化时拖拽要先还原再跟随，否则窗口位置会突然跳到屏幕外
        if (WindowState == WindowState.Maximized)
        {
            var pos = e.GetPosition(this);
            var ratio = pos.X / Math.Max(1, ActualWidth);
            WindowState = WindowState.Normal;

            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left, pos.X + Left - ActualWidth * ratio);
            Top = Math.Max(workArea.Top, 0);
        }

        try { DragMove(); } catch { /* 快速点击时 DragMove 会抛，忽略 */ }
    }

    /// <summary>判断某个视觉元素是否位于按钮内部（用于区分「拖窗口」和「点按钮」）。</summary>
    private static bool IsInButton(DependencyObject? source)
    {
        var cur = source;
        for (var i = 0; i < 12 && cur is not null; i++)
        {
            if (cur is ButtonBase) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        // WindowStyle=None + WindowChrome 时最大化交给 WindowState 即可，
        // WindowChrome 会自动按工作区（不含任务栏）计算尺寸。
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ==================== 顶部菜单 ====================

    private void OpenMenu(Button button)
    {
        var menu = button.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;
    }

    private void OnMenuFile(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuShare(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuService(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuUpdate(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);
    private void OnMenuTools(object sender, RoutedEventArgs e) => OpenMenu((Button)sender);

    // ----- 文件菜单 -----

    private void MenuOpenWeb_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(AppServices.Instance.WebUrl);

    private void MenuOpenStorage_Click(object sender, RoutedEventArgs e)
    {
        var dir = AppServices.Instance.SettingsSnapshot.StorageDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = AppServices.Instance.DefaultStorageDirectory;
        ShellHelper.OpenFolder(dir);
    }

    private void MenuOpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppServices.Instance.DataDirectory, "settings.json");
        if (!File.Exists(path))
        {
            SetStatus("设置文件尚未生成，请先保存一次设置");
            Toast("设置文件不存在", "还没有写入过 settings.json。", isError: true);
            return;
        }
        ShellHelper.RevealInExplorer(path);
    }

    private async void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) await app.ExitAsync();
    }

    // ----- 分享菜单 -----

    private void MenuNewShare_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(NewSharePage));

    private async void MenuCopyInternal_Click(object sender, RoutedEventArgs e)
        => await ShellHelper.CopyTextAsync(AppServices.Instance.InternalBase, "已复制内网地址",
            AppServices.Instance.InternalBase);

    private void MenuRefreshShares_Click(object sender, RoutedEventArgs e) => RefreshCurrentPage();

    // ----- 服务菜单 -----

    private async void MenuRestartService_Click(object sender, RoutedEventArgs e)
    {
        Busy("正在重启服务…");
        try
        {
            await AppServices.Instance.RestartServerAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("MenuRestartService", ex);
            AppServices.Instance.ReportError("重启失败", ex.Message);
        }
        finally
        {
            Unbusy();
        }
        SetStatus(AppServices.Instance.ServerStatusText);
        RefreshCurrentPage();
    }

    private async void MenuFirewallAllow_Click(object sender, RoutedEventArgs e)
    {
        var port = AppServices.Instance.SettingsSnapshot.Port;
        Busy("正在放行防火墙…");
        try
        {
            var result = await FirewallHelper.AllowPortAsync(port);
            if (result.Success)
                Toast("防火墙已放行", $"已允许 TCP {port} 的入站连接。", isSuccess: true);
            else
                AppServices.Instance.ReportError("放行防火墙失败", result.Error ?? "未知错误");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("MenuFirewallAllow", ex);
            AppServices.Instance.ReportError("放行防火墙失败", ex.Message);
        }
        finally
        {
            Unbusy();
        }
    }

    // ----- 更新菜单 -----

    private async void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // 手动检查：真发请求，结果用 Toast 汇报（AppUpdateWindow 由任务 5 交付后再换成弹窗）
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        SetStatus("正在检查更新…");
        try
        {
            var result = await UpdateService.Instance.CheckAsync();
            UpdateBadge.Visibility = result.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (result.Error is not null)
            {
                SetStatus("检查更新失败：" + result.Error);
                Toast("检查更新失败", result.Error, isError: true);
            }
            else if (result.HasUpdate)
            {
                SetStatus($"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）");
                Toast("发现新版本", $"{result.LatestVersion} 已发布，可在发布页下载。", isSuccess: true);
            }
            else
            {
                SetStatus($"已是最新版本 {result.CurrentVersion}");
                Toast("已是最新版本", $"当前版本 {result.CurrentVersion}。", isSuccess: true);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("MenuCheckUpdate", ex);
            SetStatus("检查更新失败");
            Toast("检查更新失败", ex.Message, isError: true);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void MenuToggleAutoCheck_Click(object sender, RoutedEventArgs e)
    {
        var settings = UpdateService.Instance.Settings;
        settings.Enabled = MenuAutoCheck.IsChecked;
        UpdateService.Instance.SaveSettings();

        if (settings.Enabled)
        {
            UpdateService.Instance.StartAutoCheck(this);
            SetStatus($"已开启自动检查更新（每 {settings.IntervalHours} 小时）");
        }
        else
        {
            UpdateService.Instance.StopAutoCheck();
            SetStatus("已关闭自动检查更新");
        }

        // 频率子菜单不禁用：先设频率再开启是完全合理的操作顺序（JavaDeck 踩过这个坑）
        SyncUpdateMenu();
    }

    private void MenuPickInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if (!int.TryParse(Convert.ToString(item.Tag), out var hours) || hours <= 0) return;

        var settings = UpdateService.Instance.Settings;
        settings.IntervalHours = hours;
        UpdateService.Instance.SaveSettings();
        if (settings.Enabled) UpdateService.Instance.StartAutoCheck(this);

        SyncUpdateMenu();
        SetStatus($"检查频率已设为每 {UpdateService.Instance.Settings.IntervalHours} 小时");
    }

    private void UpdateBadge_Click(object sender, RoutedEventArgs e)
    {
        ShellHelper.OpenUrl(ProjectUrl);
    }

    /// <summary>把 UpdateService 的设置同步到菜单勾选态。</summary>
    private void SyncUpdateMenu()
    {
        try
        {
            var settings = UpdateService.Instance.Settings;
            MenuAutoCheck.IsChecked = settings.Enabled;

            foreach (var item in MenuInterval.Items.OfType<MenuItem>())
            {
                item.IsChecked = int.TryParse(Convert.ToString(item.Tag), out var h)
                                 && h == settings.IntervalHours;
            }

            LastCheckText.Text = settings.LastCheck is { } last
                ? $"上次检查：{last:MM-dd HH:mm}"
                : "尚未检查更新";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SyncUpdateMenu", ex);
        }
    }

    private void StartAutoUpdateCheck()
    {
        try
        {
            var service = UpdateService.Instance;
            service.UpdateFound -= OnUpdateFound;
            service.UpdateFound += OnUpdateFound;
            service.StartAutoCheck(this);
            SyncUpdateMenu();
        }
        catch (Exception ex)
        {
            // 后台更新服务永远不能把主程序搞崩
            ErrorLog.Write("StartAutoUpdateCheck", ex);
        }
    }

    private void OnUpdateFound(UpdateCheckResult result)
    {
        UpdateBadge.Visibility = Visibility.Visible;
        UpdateBadge.ToolTip = $"{result.LatestVersion} 已发布";
        SetStatus($"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）");
    }

    // ----- 工具菜单 -----

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => NavigateTo(typeof(SettingsPage));

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        // AboutWindow 由任务 4 交付。这里不写 MessageBox（契约禁止），
        // 先把版本与项目地址写到状态条 + 轻提示，等 AboutWindow 落地后换成 ShowDialog。
        var version = SelfUpdater.CurrentVersion;
        SetStatus($"LanShare {version} · {ProjectUrl}");
        Toast("关于 LanShare", $"版本 {version} · {ProjectUrl}");
    }

    // ==================== 图标栏导航 ====================

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton item) return;
        var pageType = ResolvePage(item.Tag as string);
        if (pageType is null) return;
        NavigateTo(pageType);
    }

    private static Type? ResolvePage(string? tag) => tag switch
    {
        "overview" => typeof(OverviewPage),
        "shares" => typeof(SharesPage),
        "new" => typeof(NewSharePage),
        "settings" => typeof(SettingsPage),
        _ => null
    };

    private static string? TagOf(Type pageType) => pageType switch
    {
        var t when t == typeof(OverviewPage) => "overview",
        var t when t == typeof(SharesPage) => "shares",
        var t when t == typeof(NewSharePage) => "new",
        var t when t == typeof(SettingsPage) => "settings",
        _ => null
    };

    /// <summary>导航到指定页面。重复导航到同一页不会重建（避免页面反复订阅事件）。</summary>
    public void NavigateTo(Type pageType)
    {
        if (pageType is null) return;
        try
        {
            if (_currentPageType == pageType && _currentPage is not null)
            {
                _currentPage.ActivatePage();
                RefreshActions();
                return;
            }

            if (Activator.CreateInstance(pageType) is not ShellPage page)
            {
                AppServices.Instance.ReportError("无法打开页面", $"页面 {pageType.Name} 不是 ShellPage。");
                return;
            }

            if (_currentPage is not null)
            {
                _currentPage.StatusRequested -= OnPageStatusRequested;
                _currentPage.ToastRequested -= OnPageToastRequested;
                // 先把旧页面的按钮栏元素从视觉树摘下来，再换页。
                // 不摘的话，旧页面的 BuildActions() 元素仍挂在 PageActions 下，
                // 复用同一个 StackPanel 给新页会抛"已有父元素"（Specified element is already
                // the logical child of another element）。
                PageActions.Content = null;
            }

            _currentPage = page;
            _currentPageType = pageType;
            page.StatusRequested += OnPageStatusRequested;
            page.ToastRequested += OnPageToastRequested;

            PageHost.Content = page;
            page.ActivatePage();
            RefreshActions();
            UpdateNavSelection(pageType);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("NavigateTo " + pageType.Name, ex);
            AppServices.Instance.ReportError("无法打开页面", ex.Message);
        }
    }

    private void UpdateNavSelection(Type pageType)
    {
        var tag = TagOf(pageType);
        NavOverview.IsChecked = tag == "overview";
        NavShares.IsChecked = tag == "shares";
        NavNew.IsChecked = tag == "new";
        NavSettings.IsChecked = tag == "settings";
    }

    /// <summary>
    /// 把当前页 BuildActions() 的结果放进按钮栏左侧。
    ///
    /// 关键：每次替换前先把旧的从 ContentControl 上摘掉。
    /// 否则同一个可视化元素在切换页面后被再次挂上，会抛"已有父元素"。
    /// </summary>
    private void RefreshActions()
    {
        try
        {
            PageActions.Content = null;
            var actions = _currentPage?.BuildActions();
            if (actions is not null)
            {
                // 兜底：万一页面返回的元素已经属于别的容器，先断开再挂
                if (actions.Parent is ContentControl or Panel or Decorator)
                {
                    try
                    {
                        if (actions.Parent is ContentControl cc) cc.Content = null;
                        else if (actions.Parent is Panel p) p.Children.Remove(actions);
                        else if (actions.Parent is Decorator d) d.Child = null;
                    }
                    catch { }
                }
                PageActions.Content = actions;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RefreshActions", ex);
            PageActions.Content = null;
        }
    }

    /// <summary>刷新当前页（菜单"刷新分享列表"/F5 走这里）。</summary>
    public void RefreshCurrentPage()
    {
        try
        {
            _currentPage?.RefreshPage();
            RefreshActions();
            SetStatus("已刷新");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RefreshCurrentPage", ex);
            AppServices.Instance.ReportError("刷新失败", ex.Message);
        }
    }

    // ==================== 页面反馈通道 ====================

    private void OnPageStatusRequested(object? sender, string text) => SetStatus(text);

    private void OnPageToastRequested(object? sender, ShellToast toast)
        => Toast(toast.Title, toast.Message, toast.IsError, toast.IsSuccess);

    /// <summary>状态条文字。页面和壳层都走这里。</summary>
    public void SetStatus(string text)
    {
        void Apply()
        {
            StatusText.Text = string.IsNullOrWhiteSpace(text) ? "就绪" : text;
        }

        if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke((Action)Apply);
        else Apply();
    }

    /// <summary>
    /// 右下角轻提示。isError / isSuccess 二选一，都 false 时是普通信息。
    /// 真正的 UI 在 Controls/ToastHost（任务 1 交付），这里只做转发。
    /// </summary>
    public void Toast(string title, string message, bool isError = false, bool isSuccess = false)
    {
        var kind = isError ? ToastKind.Error : isSuccess ? ToastKind.Success : ToastKind.Info;
        try { ToastLayer?.Show(title, message, kind); }
        catch (Exception ex) { ErrorLog.Write("Toast", ex); }
    }

    /// <summary>
    /// 显示忙碌遮罩。只覆盖内容区（BusyLayer），绝不替换 Window.Content，
    /// 也绝不设 IsEnabled=false —— 那会把自绘标题栏的按钮一起废掉。
    /// 重复调用只会换成最新的文字，不会叠出多层遮罩。
    /// </summary>
    public void Busy(string text)
    {
        void Apply()
        {
            try
            {
                _busy?.Dispose();
                _busy = BusyOverlay.Show(BusyLayer, string.IsNullOrWhiteSpace(text) ? "正在处理…" : text);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Busy", ex);
            }
        }

        if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke((Action)Apply);
        else Apply();
    }

    public void Unbusy()
    {
        void Apply()
        {
            try
            {
                _busy?.Dispose();
                _busy = null;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Unbusy", ex);
            }
        }

        if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke((Action)Apply);
        else Apply();
    }

    // ==================== AppServices 事件 ====================

    private void OnStateChanged(object? sender, EventArgs e) => UpdateTrayState();

    private void OnSnackbarRequested(object? sender, SnackbarMessage message)
        => Toast(message.Title, message.Message, message.IsError, message.IsSuccess);

    private void OnAutoShareStarting()
    {
        ShowWindow();
        NavigateTo(typeof(NewSharePage));
    }

    private void OnAutoShareCreated(ShareRecord record)
    {
        ShowWindow();
        NavigateTo(typeof(NewSharePage));
    }

    private void OnShareCreatedNotification(ShareRecord record)
    {
        if (!IsVisible)
        {
            try
            {
                TrayIcon.ShowNotification("分享已创建", $"{record.DisplayName} 已就绪，双击托盘图标查看。",
                    H.NotifyIcon.Core.NotificationIcon.Info);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("TrayNotification", ex);
            }
        }
    }

    private void OnNavigationRequested(Type pageType)
    {
        ShowWindow();
        NavigateTo(pageType);
    }

    // ==================== 键盘快捷键 ====================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var pageType = e.Key switch
            {
                Key.D1 => typeof(OverviewPage),
                Key.D2 => typeof(SharesPage),
                Key.D3 => typeof(NewSharePage),
                Key.D4 => typeof(SettingsPage),
                Key.OemComma => typeof(SettingsPage),
                _ => (Type?)null
            };

            if (pageType is not null)
            {
                NavigateTo(pageType);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.N)
            {
                NavigateTo(typeof(NewSharePage));
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F5)
        {
            RefreshCurrentPage();
            e.Handled = true;
        }
    }

    // ==================== 显示 / 隐藏 ====================

    public void ShowFromExternalRequest()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFromExternalRequest);
            return;
        }
        ShowWindow();
    }

    public void ShowWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowWindow);
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        UpdateTrayState();
    }

    public void HideToTray()
    {
        if (!_trayAvailable)
        {
            // 托盘不可用时退化为最小化，避免窗口"消失"后无法找回。
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        UpdateTrayState();
    }

    // ==================== 托盘 ====================

    private void UpdateTrayState()
    {
        try
        {
            var status = AppServices.Instance.ServerStatusText;
            TrayIcon.ToolTipText = $"内网快传 · {status}";
            TrayShowHideMenuItem.Header = IsVisible ? "隐藏主窗口" : "显示主窗口";
        }
        catch
        {
            // 启动早期 TrayIcon 可能还没创建好，忽略即可
        }
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowWindow();

    private void TrayShowHide_Click(object sender, RoutedEventArgs e)
    {
        if (IsVisible) HideToTray();
        else ShowWindow();
    }

    private void TrayNewShare_Click(object sender, RoutedEventArgs e)
    {
        ShowWindow();
        NavigateTo(typeof(NewSharePage));
    }

    private void TrayOpenWeb_Click(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(AppServices.Instance.WebUrl);

    private async void TrayCopyInternal_Click(object sender, RoutedEventArgs e)
        => await ShellHelper.CopyTextAsync(AppServices.Instance.InternalBase, "已复制内网地址",
            AppServices.Instance.InternalBase);

    private void TraySettings_Click(object sender, RoutedEventArgs e)
    {
        ShowWindow();
        NavigateTo(typeof(SettingsPage));
    }

    private async void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) await app.ExitAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || (Application.Current as App)?.IsExiting == true)
        {
            try { TrayIcon.Dispose(); } catch { }
            return;
        }

        e.Cancel = true;
        if (!_trayAvailable)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        UpdateTrayState();

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            try
            {
                TrayIcon.ShowNotification("内网快传仍在运行",
                    "窗口已隐藏到系统托盘，分享服务会继续运行。双击托盘图标可重新打开。",
                    H.NotifyIcon.Core.NotificationIcon.Info);
            }
            catch { }
        }
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        try { UpdateService.Instance.StopAutoCheck(); } catch { }
        try { TrayIcon.Dispose(); } catch { }
        try { _busy?.Dispose(); } catch { }
        _busy = null;
    }

    private void TrySetTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null) TrayIcon.Icon = (System.Drawing.Icon)icon.Clone();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("TrayIconLoad", ex);
        }

        try
        {
            TrayIcon.Visibility = Visibility.Visible;
            TrayIcon.ForceCreate(false);
            _trayAvailable = TrayIcon.IsCreated;
        }
        catch (Exception ex)
        {
            _trayAvailable = false;
            ErrorLog.Write("TrayCreate", ex);
        }
    }
}
