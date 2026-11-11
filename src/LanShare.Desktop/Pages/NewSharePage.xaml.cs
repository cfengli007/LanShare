using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LanShare.Core;
using LanShare.Desktop.Models;
using LanShare.Desktop.Services;
using Microsoft.Win32;

namespace LanShare.Desktop.Pages;

/// <summary>
/// 新建分享（ShellPage 体系）。
///
/// 坑记：
///   • 自定义有效期改成普通 TextBox：WPF-UI 的 NumberBox 已随依赖移除，
///     数字校验自己写在 PreviewTextInput（挡非数字）与 ReadOptions（越界夹紧）里，
///     不要在输入过程中抛异常打断用户。
///   • 创建中的进度不用 ProgressRing（同样来自 WPF-UI），改用状态文字 + 禁用按钮；
///     需要更重的忙碌反馈时由 Shell.Busy() 统一处理。
///   • 右键菜单分享的结果通过 AppServices.ShareResultAvailable 事件推到本页，
///     事件可能来自非 UI 线程，必须 AppServices.OnUi 包一层。
///   • 底部没有主操作按钮（BuildActions 返回 null），创建按钮就在页面内。
/// </summary>
public partial class NewSharePage : ShellPage
{
    public sealed record SelectedPath(string Path)
    {
        public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : Path;
    }

    private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.Compiled);

    private readonly ObservableCollection<SelectedPath> _paths = new();
    private ShareRecord? _lastResult;
    private bool _creating;
    private bool _initialized;

    public NewSharePage()
    {
        InitializeComponent();
        FileList.ItemsSource = _paths;

        AppServices.Instance.ShareResultAvailable += OnShareResultAvailable;
        AppServices.Instance.AutoShareStarting += OnAutoShareStarting;
        Loaded += OnLoaded;
    }

    // ===================== ShellPage 生命周期 =====================

    /// <summary>底部无主操作：创建按钮在页面内。</summary>
    public override FrameworkElement? BuildActions() => null;

    public override void ActivatePage() => UpdateSelectionState();

    public override void RefreshPage() => UpdateSelectionState();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            ApplyDefaultExpiry();
        }

        UpdateSelectionState();

        var pending = AppServices.Instance.TakePendingShareResult();
        if (pending is not null)
        {
            ShowResult(pending);
        }
    }

    private void ApplyDefaultExpiry()
    {
        var minutes = AppServices.Instance.SettingsSnapshot.DefaultExpiryMinutes;
        var known = new[] { 10, 60, 360, 1440, 10080 };
        var index = Array.IndexOf(known, minutes);
        if (index >= 0)
        {
            ExpiryCombo.SelectedIndex = index;
        }
        else
        {
            ExpiryCombo.SelectedIndex = 5;
            CustomExpiryBox.Text = Math.Clamp(minutes, 1, 43200).ToString();
        }
    }

    private void OnShareResultAvailable(ShareRecord record)
    {
        AppServices.OnUi(() => ShowResult(record));
    }

    private void Page_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        PageScrollViewer.ScrollToVerticalOffset(PageScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnAutoShareStarting()
    {
        AppServices.OnUi(() =>
        {
            CreateStatusText.Text = "正在为右键选择的项目创建分享…";
            SetStatus("正在创建分享…");
        });
    }

    // ===================== 选择内容 =====================

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要分享的文件",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要分享的文件夹",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            AddPaths(dialog.FolderNames);
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                if (_paths.Any(p => string.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
                _paths.Add(new SelectedPath(full));
                added++;
            }
            catch (Exception ex)
            {
                AppServices.Instance.ReportError("添加失败", $"{path}：{ex.Message}");
            }
        }

        if (added > 0)
        {
            CreateStatusText.Text = string.Empty;
            HideBanner();
        }
        UpdateSelectionState();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SelectedPath item) return;
        _paths.Remove(item);
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var hasFiles = _paths.Count > 0;
        EmptyFilesPanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        SelectionSummaryText.Text = hasFiles
            ? $"已选择 {_paths.Count} 项"
            : "未选择任何内容";
        if (!_creating)
        {
            CreateButton.IsEnabled = hasFiles;
        }
    }

    // ===================== 有效期 =====================

    private void ExpiryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 里 SelectedIndex="3" 会在解析期就触发本事件，此时排在后面的
        // CustomExpiryPanel / CustomExpiryBox 可能还没构造出来，必须容忍 null。
        if (CustomExpiryPanel is null || CustomExpiryBox is null) return;
        var isCustom = (ExpiryCombo.SelectedItem as ComboBoxItem)?.Tag as string == "custom";
        CustomExpiryPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (isCustom && string.IsNullOrWhiteSpace(CustomExpiryBox.Text)) CustomExpiryBox.Text = "60";
    }

    private void CustomExpiry_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnly.IsMatch(e.Text);
    }

    private void CustomExpiry_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 坑：XAML 里 CustomExpiryBox 带 Text="60"，解析期就会触发 TextChanged，
        // 而排在它后面的 CustomExpiryHelp 那时还是 null —— 直接崩掉整个页面构造。
        // 所有"XAML 挂事件 + handler 里访问其它 x:Name 控件"的地方都要这样兜底。
        if (CustomExpiryHelp is null) return;

        // 只提示，不夹紧：用户可能正输到一半。
        var text = (sender as TextBox)?.Text ?? CustomExpiryBox?.Text ?? string.Empty;
        if (!int.TryParse(text, out var minutes))
        {
            CustomExpiryHelp.Text = "请输入 1 – 43200 之间的整数分钟数。";
            return;
        }

        CustomExpiryHelp.Text = minutes is < 1 or > 43200
            ? "超出范围，将按 1 – 43200 夹紧处理。"
            : string.Empty;
    }

    private (int Minutes, bool Burn) ReadOptions()
    {
        var tag = (ExpiryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        int minutes;
        if (tag == "custom")
        {
            minutes = int.TryParse(CustomExpiryBox.Text, out var typed) ? typed : 60;
            minutes = Math.Clamp(minutes, 1, 43200);
        }
        else
        {
            minutes = int.TryParse(tag, out var parsed) ? parsed : 1440;
        }

        return (minutes, BurnToggle.IsChecked == true);
    }

    // ===================== 创建 =====================

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_creating) return;
        if (_paths.Count == 0)
        {
            ShowBanner("Warn", "还没有选择内容：请先添加要分享的文件或文件夹。");
            SetStatus("还没有选择内容");
            return;
        }

        _creating = true;
        CreateButton.IsEnabled = false;
        CreateStatusText.Text = "正在创建分享…";
        SetStatus("正在创建分享…");
        HideBanner();

        try
        {
            var (minutes, burn) = ReadOptions();
            var record = await AppServices.Instance.CreateShareAsync(
                _paths.Select(p => p.Path).ToList(), minutes, burn);
            ShowResult(record);
            CreateStatusText.Text = "分享已创建。";
            SetStatus("分享已创建");
            Toast("分享已创建", record.DisplayName, isSuccess: true);
        }
        catch (Exception ex)
        {
            CreateStatusText.Text = string.Empty;
            ShowBanner("Danger", "创建分享失败：" + ex.Message);
            SetStatus("创建分享失败");
            AppServices.Instance.ReportError("创建分享失败", ex.Message);
        }
        finally
        {
            _creating = false;
            CreateButton.IsEnabled = _paths.Count > 0;
        }
    }

    private void ShowResult(ShareRecord record)
    {
        try
        {
            _lastResult = record;
            var services = AppServices.Instance;

            ResultName.Text = record.DisplayName;
            var source = record.Source == "web" ? "网页上传" : "桌面端";
            var expiry = record.BurnAfterDownload
                ? "下载一次后失效"
                : $"有效期至 {record.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            ResultMeta.Text = $"{ShareRow.FormatBytes(record.TotalBytes)} · {source} · {expiry}";

            ResultInternalBox.Text = services.InternalShareUrl(record);
            ResultExternalBox.Text = services.ExternalShareUrl(record);

            ResultEmptyPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Show share result", ex);
        }
    }

    // ===================== 结果操作 =====================

    private async void CopyInternal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var url = AppServices.Instance.InternalShareUrl(_lastResult);
        await ShellHelper.CopyTextAsync(url, "已复制内网链接", url);
    }

    private async void CopyExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var services = AppServices.Instance;
        var url = services.ExternalShareUrl(_lastResult);
        if (string.Equals(url, services.InternalShareUrl(_lastResult), StringComparison.OrdinalIgnoreCase))
        {
            Toast("未配置外网地址", "请先在“设置 → 网络”里填写外网访问前缀。", isError: true);
            SetStatus("未配置外网地址");
            return;
        }
        await ShellHelper.CopyTextAsync(url, "已复制外网链接", url);
    }

    private void OpenInternal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        ShellHelper.OpenUrl(AppServices.Instance.InternalShareUrl(_lastResult));
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var services = AppServices.Instance;
        var url = services.ExternalShareUrl(_lastResult);
        if (string.Equals(url, services.InternalShareUrl(_lastResult), StringComparison.OrdinalIgnoreCase))
        {
            Toast("未配置外网地址", "当前只能使用内网链接。", isError: true);
            SetStatus("未配置外网地址");
            return;
        }
        ShellHelper.OpenUrl(url);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastResult is not null && System.IO.File.Exists(_lastResult.DownloadPath))
            {
                ShellHelper.RevealInExplorer(_lastResult.DownloadPath);
                return;
            }
            ShellHelper.OpenFolder(AppServices.Instance.IsRuntimeAvailable
                ? AppServices.Instance.SettingsSnapshot.StorageDirectory
                : AppServices.Instance.DefaultStorageDirectory);
        }
        catch (Exception ex)
        {
            AppServices.Instance.ReportError("打开文件夹失败", ex.Message);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _paths.Clear();
        _lastResult = null;
        ResultPanel.Visibility = Visibility.Collapsed;
        ResultEmptyPanel.Visibility = Visibility.Visible;
        CreateStatusText.Text = string.Empty;
        HideBanner();
        ApplyDefaultExpiry();
        UpdateSelectionState();
        SetStatus("已清空，可以再分享一批");
    }

    // ===================== 提示条 =====================

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
}
