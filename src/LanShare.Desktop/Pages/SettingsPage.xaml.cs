using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LanShare.Core;
using LanShare.Desktop.Dialogs;
using LanShare.Desktop.Services;
using LanShare.Desktop.Services.Update;
using Microsoft.Win32;

namespace LanShare.Desktop.Pages;

/// <summary>
/// 设置页（ShellPage 体系）。
///
/// 坑记：
///   • 数字输入全部是普通 TextBox + PreviewTextInput 过滤非数字，
///     真正的越界夹紧放在保存时统一做（用户可能正输到一半，边输边夹会很烦）。
///   • 主题 ComboBox 一旦变化就立刻 ThemeService.Apply 做即时预览，
///     但载入配置期间（_loading）要挡住，否则会把半载入的状态写进主题服务。
///   • 卸载右键菜单 / 移除防火墙规则这类危险操作，用页面内自绘的 ConfirmBar
///     二次确认，不用 MessageBox（不跟主题、不能复制）。
///   • “检查更新”目前只负责打开 Releases 页面：自更新服务（SelfUpdater /
///     AppUpdateWindow）由其它任务交付，交付后在这里替换成真实检查即可，
///     不要为了它引入对未交付类型的一刀切依赖。
///   • 版本号优先取 AssemblyInformationalVersion（去掉 +sha 后缀），
///     回退 AssemblyName.Version，与自更新模块的取法保持一致。
/// </summary>
public partial class SettingsPage : ShellPage
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.Compiled);
    private const string ProjectUrl = "https://github.com/cfengli007/LanShare";

    private bool _loading;
    private bool _saving;
    private bool _ready;
    private Action? _pendingConfirm;

    public SettingsPage()
    {
        InitializeComponent();
        AppServices.Instance.SettingsChanged += OnSettingsChanged;
        Loaded += OnLoaded;
        _ready = true;   // 必须放在 InitializeComponent 之后：解析期的事件要据此跳过
    }

    // ===================== ShellPage 生命周期 =====================

    public override void ActivatePage()
    {
        if (!_loading && !_saving) LoadSettings();
    }

    public override void RefreshPage() => LoadSettings();

    /// <summary>底部按钮栏主操作：重新载入 + 保存设置。</summary>
    public override FrameworkElement? BuildActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var reload = new Button
        {
            Content = "重新载入",
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        reload.Click += Reload_Click;

        var save = new Button
        {
            Content = "保存设置",
            Style = (Style)FindResource("PrimaryButton")
        };
        save.Click += Save_Click;

        panel.Children.Add(reload);
        panel.Children.Add(save);
        return panel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_loading) LoadSettings();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppServices.OnUi(() =>
        {
            if (!_loading && !_saving) LoadSettings();
        });
    }

    // ===================== 载入 =====================

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var settings = AppServices.Instance.SettingsSnapshot;

            BindAddressCombo.Items.Clear();
            BindAddressCombo.Items.Add("0.0.0.0");
            BindAddressCombo.Items.Add("127.0.0.1");
            foreach (var address in NetworkInfo.GetIPv4Addresses())
            {
                if (!BindAddressCombo.Items.Contains(address.Address)) BindAddressCombo.Items.Add(address.Address);
            }
            BindAddressCombo.Text = settings.BindAddress;

            PortBox.Text = settings.Port.ToString();
            InternalBaseBox.Text = settings.InternalBaseUrl;
            ExternalBaseBox.Text = settings.ExternalBaseUrl;
            ThemeCombo.SelectedIndex = settings.Theme switch
            {
                ThemeService.Light => 1,
                ThemeService.System => 2,
                _ => 0
            };
            DefaultExpiryBox.Text = settings.DefaultExpiryMinutes.ToString();
            BurnToggle.IsChecked = settings.BurnAfterDownloadByDefault;
            MaxUploadBox.Text = Math.Max(1, Math.Round(settings.MaxUploadBytes / 1024d / 1024d)).ToString("0");
            StorageBox.Text = string.IsNullOrWhiteSpace(settings.StorageDirectory)
                ? AppServices.Instance.DefaultStorageDirectory
                : settings.StorageDirectory;

            LaunchAtStartupToggle.IsChecked = StartupManager.IsEnabled();
            NotifyToggle.IsChecked = settings.NotifyOnShareCreated;

            VersionText.Text = $"LanShare {ReadVersion()}";
            UpdateContextMenuStatus();
            HideBanner();
        }
        catch (Exception ex)
        {
            ShowBanner("Danger", "载入设置失败", ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>与自更新模块一致的取版本方式：InformationalVersion 优先，去掉 +sha。</summary>
    private static string ReadVersion()
    {
        try
        {
            var assembly = typeof(SettingsPage).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }
            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    private void UpdateContextMenuStatus()
    {
        ContextMenuStatusText.Text = ContextMenuInstaller.Describe(ContextMenuInstaller.Detect());
    }

    // ===================== 保存 =====================

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;

        if (sender is Button button) button.IsEnabled = false;
        SetStatus("正在保存设置…");
        SaveStatusText.Text = "正在保存…";
        HideBanner();
        CancelConfirm();

        try
        {
            var settings = AppServices.Instance.SettingsSnapshot;
            settings.BindAddress = (BindAddressCombo.Text ?? string.Empty).Trim();
            settings.Port = ParseInt(PortBox.Text, AppSettings.DefaultPort, 1, 65535);
            settings.InternalBaseUrl = (InternalBaseBox.Text ?? string.Empty).Trim();
            settings.ExternalBaseUrl = (ExternalBaseBox.Text ?? string.Empty).Trim();
            settings.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? ThemeService.Dark;
            settings.DefaultExpiryMinutes = ParseInt(DefaultExpiryBox.Text, 1440, 1, 43200);
            settings.BurnAfterDownloadByDefault = BurnToggle.IsChecked == true;

            var uploadMb = ParseInt(MaxUploadBox.Text, 2048, 1, 102400);
            settings.MaxUploadBytes = (long)uploadMb * 1024L * 1024L;
            settings.StorageDirectory = (StorageBox.Text ?? string.Empty).Trim();
            settings.LaunchAtStartup = LaunchAtStartupToggle.IsChecked == true;
            settings.NotifyOnShareCreated = NotifyToggle.IsChecked == true;
            settings.ContextMenuInstalled = ContextMenuInstaller.IsInstalled();

            await AppServices.Instance.ApplySettingsAsync(settings);
            ThemeService.Apply(settings.Theme);

            string? startupError = null;
            try
            {
                StartupManager.SetEnabled(settings.LaunchAtStartup);
            }
            catch (Exception ex)
            {
                startupError = ex.Message;
                ErrorLog.Write("StartupManager.SetEnabled", ex);
            }

            if (startupError is null)
            {
                SaveStatusText.Text = "已保存。";
                SetStatus("设置已保存");
                ShowBanner("Ok", "设置已保存",
                    $"服务已按 {settings.BindAddress}:{settings.Port} 重新加载。");
                Toast("设置已保存", "服务已按新配置重新加载。", isSuccess: true);
            }
            else
            {
                SaveStatusText.Text = "已保存（开机自启未生效）。";
                SetStatus("设置已保存，开机自启失败");
                ShowBanner("Warn", "设置已保存，但开机自启设置失败", startupError);
                Toast("开机自启未生效", startupError, isError: true);
            }
            UpdateContextMenuStatus();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SaveSettings", ex);
            SaveStatusText.Text = "保存失败。";
            SetStatus("保存设置失败");
            ShowBanner("Danger", "保存设置失败", ex.Message);
        }
        finally
        {
            _saving = false;
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SaveStatusText.Text = "已重新载入当前配置。";
        SetStatus("已重新载入当前配置");
        Toast("已重新载入", "当前配置已刷新。");
    }

    private void BrowseStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择分享文件存储目录" };
        if (dialog.ShowDialog() == true)
        {
            StorageBox.Text = dialog.FolderName;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 坑：XAML 里 SelectedIndex="0" 会在解析期就触发本事件，此时 _ready 还是 false，
        // ThemeCombo 这个字段本身也可能尚未连接。不挡的话轻则提前改主题，重则空引用。
        if (!_ready || _loading) return;
        var theme = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(theme)) return;
        try
        {
            ThemeService.Apply(theme);
            SetStatus("主题已切换，保存后写入配置");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Preview theme", ex);
        }
    }

    // ===================== 系统集成 =====================

    private async void InstallContextMenu_Click(object sender, RoutedEventArgs e)
    {
        ErrorLog.Write("InstallContextMenu start", null);
        ContextMenuStatusText.Text = "正在安装右键菜单…";
        SetStatus("正在安装右键菜单…");
        try
        {
            var result = await Task.Run(ContextMenuInstaller.Install);
            ErrorLog.Write("InstallContextMenu finished", null);
            UpdateContextMenuStatus();
            Toast("右键菜单已安装", result.Message, isSuccess: true);
            SetStatus("右键菜单已安装");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("InstallContextMenu", ex);
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "contextmenu-error.log"),
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never take the app down.
            }

            ContextMenuStatusText.Text = "安装失败：" + ex.Message;
            SetStatus("安装右键菜单失败");
            ShowBanner("Danger", "安装右键菜单失败", ex.Message);
            AppServices.Instance.ReportError("安装右键菜单失败", ex.Message);
        }
    }

    private void UninstallContextMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirm("确定要移除资源管理器中的“使用内网快传分享”右键菜单吗？", async () =>
        {
            ContextMenuStatusText.Text = "正在移除右键菜单…";
            SetStatus("正在移除右键菜单…");
            try
            {
                await Task.Run(ContextMenuInstaller.Uninstall);
                UpdateContextMenuStatus();
                Toast("右键菜单已卸载", "已从资源管理器移除。", isSuccess: true);
                SetStatus("右键菜单已卸载");
            }
            catch (Exception ex)
            {
                SetStatus("卸载右键菜单失败");
                ShowBanner("Danger", "卸载右键菜单失败", ex.Message);
                AppServices.Instance.ReportError("卸载右键菜单失败", ex.Message);
            }
        });
    }

    private async void AllowFirewall_Click(object sender, RoutedEventArgs e)
    {
        var port = ParseInt(PortBox.Text, AppSettings.DefaultPort, 1, 65535);
        FirewallStatusText.Text = "正在请求管理员权限…";
        SetStatus($"正在为 TCP {port} 请求管理员权限…");
        try
        {
            var result = await FirewallHelper.AllowPortAsync(port);
            if (result.Success)
            {
                FirewallStatusText.Text = $"已放行 TCP {port}（专用/域网络）。";
                SetStatus($"已放行 TCP {port}");
                Toast("防火墙已放行", $"TCP {port} 已允许局域网访问。", isSuccess: true);
            }
            else
            {
                FirewallStatusText.Text = "未放行：" + result.Error;
                SetStatus("防火墙放行失败");
                ShowBanner("Warn", "防火墙放行失败", result.Error ?? "未知错误");
                AppServices.Instance.ReportError("防火墙放行失败", result.Error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = "未放行：" + ex.Message;
            SetStatus("防火墙放行失败");
            ShowBanner("Danger", "防火墙放行失败", ex.Message);
            AppServices.Instance.ReportError("防火墙放行失败", ex.Message);
        }
    }

    private void RemoveFirewall_Click(object sender, RoutedEventArgs e)
    {
        ShowConfirm("确定要移除内网快传添加的防火墙规则吗？移除后局域网访问可能被系统阻止。", async () =>
        {
            SetStatus("正在移除防火墙规则…");
            try
            {
                var result = await FirewallHelper.RemoveRulesAsync();
                if (result.Success)
                {
                    FirewallStatusText.Text = "已移除内网快传的防火墙规则。";
                    SetStatus("防火墙规则已移除");
                    Toast("防火墙规则已移除", "局域网访问可能被系统阻止。", isSuccess: true);
                }
                else
                {
                    FirewallStatusText.Text = "移除失败：" + result.Error;
                    SetStatus("移除防火墙规则失败");
                    ShowBanner("Danger", "移除防火墙规则失败", result.Error ?? "未知错误");
                    AppServices.Instance.ReportError("移除失败", result.Error ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                SetStatus("移除防火墙规则失败");
                ShowBanner("Danger", "移除防火墙规则失败", ex.Message);
                AppServices.Instance.ReportError("移除失败", ex.Message);
            }
        });
    }

    private void ModifyIp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ModifyIpDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        LoadSettings();
    }

    // ===================== 关于 =====================

    private void OpenProject_Click(object sender, RoutedEventArgs e) => ShellHelper.OpenUrl(ProjectUrl);

    /// <summary>
    /// 手动检查更新。逻辑与 MainWindow 的「更新 → 立即检查更新」菜单一致：
    /// 真发请求 → 用 Toast/状态条汇报结果，不自己画更新窗口。
    /// （AppUpdateWindow 的弹窗式流程留给主窗口那一侧，这里保持轻量。）
    /// </summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        SetStatus("正在检查更新…");
        HideBanner();

        try
        {
            var result = await UpdateService.Instance.CheckAsync();
            if (result.Error is not null)
            {
                SetStatus("检查更新失败：" + result.Error);
                ShowBanner("Danger", "检查更新失败", result.Error);
                Toast("检查更新失败", result.Error, isError: true);
            }
            else if (result.HasUpdate)
            {
                SetStatus($"发现新版本 {result.LatestVersion}（当前 {result.CurrentVersion}）");
                ShowBanner("Ok", $"发现新版本 {result.LatestVersion}",
                    $"当前版本 {result.CurrentVersion}，可到发布页下载最新安装包。");
                Toast("发现新版本", $"{result.LatestVersion} 已发布。", isSuccess: true);
            }
            else
            {
                SetStatus($"已是最新版本 {result.CurrentVersion}");
                ShowBanner("Ok", "已是最新版本", $"当前版本 {result.CurrentVersion}。");
                Toast("已是最新版本", $"当前版本 {result.CurrentVersion}。", isSuccess: true);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SettingsCheckUpdate", ex);
            SetStatus("检查更新失败");
            ShowBanner("Danger", "检查更新失败", ex.Message);
            Toast("检查更新失败", ex.Message, isError: true);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    // ===================== 确认条 / 提示条 =====================

    /// <summary>页面内二次确认：不弹 MessageBox，确认后才执行动作。</summary>
    private void ShowConfirm(string message, Action onConfirm)
    {
        _pendingConfirm = onConfirm;
        ConfirmMessageText.Text = message;
        ConfirmBar.Visibility = Visibility.Visible;
        SetStatus("等待确认");
    }

    private void CancelConfirm()
    {
        _pendingConfirm = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        var action = _pendingConfirm;
        CancelConfirm();
        action?.Invoke();
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        CancelConfirm();
        SetStatus("已取消");
    }

    private void ShowBanner(string severity, string title, string message)
    {
        var (soft, strong) = severity switch
        {
            "Ok" => ("OkSoft", "Ok"),
            "Warn" => ("WarnSoft", "Warn"),
            _ => ("DangerSoft", "Danger")
        };

        PageBanner.Background = (System.Windows.Media.Brush)FindResource(soft);
        PageBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(strong);
        PageBannerTitle.Foreground = (System.Windows.Media.Brush)FindResource(strong);
        PageBannerTitle.Text = title;
        PageBannerText.Text = message;
        PageBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner() => PageBanner.Visibility = Visibility.Collapsed;

    private void CloseBanner_Click(object sender, RoutedEventArgs e) => HideBanner();

    // ===================== 输入辅助 =====================

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse((text ?? string.Empty).Trim(), out var value)) value = fallback;
        return Math.Clamp(value, min, max);
    }
}
