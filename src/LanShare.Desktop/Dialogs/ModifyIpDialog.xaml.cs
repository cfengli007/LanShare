using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LanShare.Desktop.Services;

namespace LanShare.Desktop.Dialogs;

/// <summary>提示条语气（替代原来的 Wpf.Ui InfoBarSeverity，仅用于选配色）。</summary>
internal enum IpInfoKind
{
    Info,
    Warning,
    Success,
    Error
}

/// <summary>
/// 修改本机 IP。
///
/// ⚠️ 业务逻辑与原实现完全一致：调用 NetworkConfigurator.GetAdapters / ApplyDhcpAsync /
///    ApplyStaticAsync 的方式、DHCP 与静态的切换、UAC 提权触发时机、成功后的延时刷新，
///    一行都没有改动。本文件只做了 UI 层替换：
///      • FluentWindow → Window（WindowStyle=None + WindowChrome，标题栏自绘）
///      • ui:NumberBox → 原生 TextBox，值解析在 ReadPrefixLength()
///      • ui:TextBox.PlaceholderText → 控件下方的 HintText
///      • ui:InfoBar → 本地 Border（IpInfoBar + IpInfoTitle + IpInfoMessage），三色由 ShowInfo 设置
///      • ui:ProgressRing → XAML 里 Ellipse + RotateTransform 动画
///
/// 控件字段名全部与 XAML 对应，改名会让设计时数据绑不上，注意别动。
/// </summary>
public partial class ModifyIpDialog : Window
{
    private bool _busy;

    public ModifyIpDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        SourceInitialized += (_, _) => ThemeService.AttachWindow(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective);
        RefreshAdapters();
    }

    private void RefreshAdapters()
    {
        try
        {
            var adapters = NetworkConfigurator.GetAdapters();
            AdapterCombo.ItemsSource = adapters;
            if (adapters.Count > 0)
            {
                AdapterCombo.SelectedIndex = 0;
            }
            else
            {
                ShowInfo(IpInfoKind.Warning, "没有可用的网卡", "未找到已连接的以太网或无线网卡。");
            }
        }
        catch (Exception ex)
        {
            ShowInfo(IpInfoKind.Error, "读取网卡失败", ex.Message);
        }
    }

    private void AdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterCombo.SelectedItem is not AdapterOption adapter) return;

        IpBox.Text = adapter.Address;
        PrefixBox.Text = adapter.PrefixLength.ToString();
        GatewayBox.Text = adapter.Gateway;
        DhcpRadio.IsChecked = adapter.DhcpEnabled;
        StaticRadio.IsChecked = !adapter.DhcpEnabled;
        UpdateStaticPanel();
    }

    private void IpMode_Checked(object sender, RoutedEventArgs e) => UpdateStaticPanel();

    private void UpdateStaticPanel()
    {
        if (StaticPanel is null) return;
        StaticPanel.IsEnabled = StaticRadio.IsChecked == true;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (AdapterCombo.SelectedItem is not AdapterOption adapter)
        {
            ShowInfo(IpInfoKind.Warning, "请选择网卡", "没有可修改的网络适配器。");
            return;
        }

        _busy = true;
        ApplyButton.IsEnabled = false;
        ApplyProgress.Visibility = Visibility.Visible;
        IpInfoBar.Visibility = Visibility.Collapsed;

        var useDhcp = DhcpRadio.IsChecked == true;
        ApplyStatusText.Text = useDhcp ? "正在切换为自动获取…" : "正在应用静态 IP…";

        try
        {
            var result = useDhcp
                ? await NetworkConfigurator.ApplyDhcpAsync(adapter.Name)
                : await NetworkConfigurator.ApplyStaticAsync(
                    adapter.Name,
                    (IpBox.Text ?? string.Empty).Trim(),
                    ReadPrefixLength(),
                    (GatewayBox.Text ?? string.Empty).Trim());

            if (result.Success)
            {
                ApplyStatusText.Text = "已应用。";
                ShowInfo(IpInfoKind.Success, "IP 配置已更新",
                    useDhcp ? "该网卡已切换为自动获取 IP。" : "静态 IP 已写入，若网络短暂中断请稍候。");
                await Task.Delay(800);
                RefreshAdapters();
            }
            else
            {
                ApplyStatusText.Text = "未应用。";
                ShowInfo(IpInfoKind.Error, "修改 IP 失败", result.Error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            ApplyStatusText.Text = "未应用。";
            ShowInfo(IpInfoKind.Error, "修改 IP 失败", ex.Message);
        }
        finally
        {
            _busy = false;
            ApplyButton.IsEnabled = true;
            ApplyProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 读子网前缀。原代码是 ui:NumberBox.Value（double?），这里换成 TextBox 后要自己解析：
    /// 空 / 非法一律回落 24，再夹到 1..32 —— 与原来 NumberBox Minimum/Maximum 的行为等价。
    /// </summary>
    private int ReadPrefixLength()
    {
        var text = (PrefixBox.Text ?? string.Empty).Trim();
        var value = int.TryParse(text, out var parsed) ? parsed : 24;
        return Math.Clamp(value, 1, 32);
    }

    private void OpenNetworkConnections_Click(object sender, RoutedEventArgs e)
    {
        try { ShellHelper.OpenNetworkConnections(); }
        catch (Exception ex) { ShowInfo(IpInfoKind.Error, "无法打开网络连接", ex.Message); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

    /// <summary>
    /// 显示提示条。配色全部走主题键：错误 Danger/DangerSoft、警告 Warn/WarnSoft、
    /// 成功 Ok/OkSoft、普通 Accent/AccentSoft（与 InfoBanner 组件的规范一致）。
    /// 用 SetResourceReference 而非 FindResource，这样窗口开着时切主题也会跟着变。
    /// </summary>
    private void ShowInfo(IpInfoKind kind, string title, string message)
    {
        var (borderKey, backKey, titleKey) = kind switch
        {
            IpInfoKind.Error => ("Danger", "DangerSoft", "Danger"),
            IpInfoKind.Warning => ("Warn", "WarnSoft", "Warn"),
            IpInfoKind.Success => ("Ok", "OkSoft", "Ok"),
            _ => ("Accent", "AccentSoft", "Accent")
        };

        IpInfoBar.SetResourceReference(Border.BorderBrushProperty, borderKey);
        IpInfoBar.SetResourceReference(Border.BackgroundProperty, backKey);
        IpInfoTitle.SetResourceReference(TextBlock.ForegroundProperty, titleKey);

        IpInfoBar.CornerRadius = new CornerRadius(8);
        IpInfoTitle.Text = title;
        IpInfoMessage.Text = message;
        IpInfoBar.Visibility = Visibility.Visible;
    }
}
