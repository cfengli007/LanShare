using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LanShare.Desktop.Models;
using LanShare.Desktop.Services;

namespace LanShare.Desktop.Pages;

/// <summary>
/// 我的分享（ShellPage 体系）。
///
/// 坑记：
///   • 表头与行的列宽必须写死成同一组数字，否则 ListView 的 ItemsPresenter
///     会把内容再套一层边框，表头和行就错位了。名称列用 * + MinWidth 兜底。
///   • 终止分享要二次确认：这里用页面内自绘的 ConfirmBar，不用 MessageBox
///     （MessageBox 不跟主题走，也不能复制文本）。
///   • 每秒 Tick 一次刷新倒计时，Unloaded 时务必停表，否则页面切走后仍在跑。
///   • 页面整体滚动放在外层 ScrollViewer，ListView 自己关掉纵向滚动条，
///     鼠标滚轮由 Page_PreviewMouseWheel 转交给外层。
/// </summary>
public partial class SharesPage : ShellPage
{
    private readonly ObservableCollection<ShareRow> _rows = new();
    private readonly DispatcherTimer _timer;
    private bool _refreshing;
    private ShareRow? _pendingRevoke;

    public SharesPage()
    {
        InitializeComponent();
        SharesList.ItemsSource = _rows;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            foreach (var row in _rows) row.Tick();
        };

        AppServices.Instance.SharesChanged += OnSharesChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ===================== ShellPage 生命周期 =====================

    public override void ActivatePage()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public override void RefreshPage() => _ = RefreshAsync();

    /// <summary>底部按钮栏主操作：刷新 + 新建分享。</summary>
    public override FrameworkElement? BuildActions()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var refresh = new Button
        {
            Content = "刷新",
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        refresh.Click += Refresh_Click;

        var newShare = new Button
        {
            Content = "新建分享",
            Style = (Style)FindResource("PrimaryButton")
        };
        newShare.Click += NewShare_Click;

        panel.Children.Add(refresh);
        panel.Children.Add(newShare);
        return panel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        await RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void Page_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        PageScrollViewer.ScrollToVerticalOffset(PageScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnSharesChanged(object? sender, EventArgs e)
    {
        AppServices.OnUi(() => _ = RefreshAsync());
    }

    // ===================== 数据 =====================

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var services = AppServices.Instance;
            var shares = await services.LoadSharesAsync();

            _rows.Clear();
            foreach (var record in shares)
            {
                _rows.Add(new ShareRow(record, services.InternalShareUrl(record), services.ExternalShareUrl(record)));
            }

            var isEmpty = _rows.Count == 0;
            EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            SharesList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            CountText.Text = isEmpty ? string.Empty : $"共 {_rows.Count} 条分享";
            SetStatus(isEmpty ? "就绪" : $"共 {_rows.Count} 条分享");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Shares refresh", ex);
            ShowBanner("Danger", "加载分享失败：" + ex.Message);
            SetStatus("加载分享失败");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowBanner(string severity, string text)
    {
        var (soft, strong) = severity switch
        {
            "Ok" => ("OkSoft", "Ok"),
            "Warn" => ("WarnSoft", "Warn"),
            _ => ("DangerSoft", "Danger")
        };

        PageBanner.Background = (System.Windows.Media.Brush)FindResource(soft);
        PageBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(strong);
        PageBannerText.Foreground = (System.Windows.Media.Brush)FindResource(strong);
        PageBannerText.Text = text;
        PageBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner() => PageBanner.Visibility = Visibility.Collapsed;

    // ===================== 命令 =====================

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        HideBanner();
        await RefreshAsync();
        Toast("已刷新", "分享列表已更新。");
    }

    private void NewShare_Click(object sender, RoutedEventArgs e) =>
        AppServices.Instance.RequestNavigation(typeof(NewSharePage));

    private async void CopyInternal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;
        await ShellHelper.CopyTextAsync(row.InternalUrl, "已复制内网链接", row.InternalUrl);
    }

    private async void CopyExternal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;
        if (row.ExternalUrl == row.InternalUrl)
        {
            Toast("未配置外网地址", "请先在“设置 → 网络”里填写外网访问前缀。", isError: true);
            SetStatus("未配置外网地址");
            return;
        }
        await ShellHelper.CopyTextAsync(row.ExternalUrl, "已复制外网链接", row.ExternalUrl);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;
        ShellHelper.OpenUrl(row.InternalUrl);
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShareRow row) return;

        _pendingRevoke = row;
        ConfirmText.Text = $"确定要终止分享“{row.DisplayName}”吗？终止后链接立即失效，已分享的文件会从存储目录删除。";
        ConfirmBar.Visibility = Visibility.Visible;
        SetStatus("等待确认终止分享");
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        _pendingRevoke = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
        SetStatus("已取消终止");
    }

    private async void ConfirmRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRevoke is not { } row) return;
        _pendingRevoke = null;
        ConfirmBar.Visibility = Visibility.Collapsed;

        SetStatus("正在终止分享…");
        try
        {
            await AppServices.Instance.RevokeAsync(row.Id);
            Toast("已终止分享", row.DisplayName + " 已删除", isSuccess: true);
            SetStatus("已终止分享");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("终止分享失败", ex.Message);
            ShowBanner("Danger", "终止分享失败：" + ex.Message);
            SetStatus("终止分享失败");
        }
    }
}
