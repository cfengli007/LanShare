using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LanShare.Desktop.Services;

namespace LanShare.Desktop.Dialogs;

/// <summary>消息框的语气。决定左侧色条颜色与窗口标题后缀。</summary>
public enum MsgKind
{
    Info,
    Warning,
    Error
}

/// <summary>
/// 自绘消息框，用来替代系统 MessageBox。
///
/// 为什么不用 MessageBox：它不跟随深浅主题（深色界面里会弹出一个刺眼的白框），
/// 而且正文不能选中——异常堆栈根本抄不出来，用户只能截图。
///
/// 用法：
///   MsgWindow.Show(this, "修改 IP 失败", ex.ToString(), MsgKind.Error);
///   if (MsgWindow.Confirm(this, "终止分享", "确定要终止该分享吗？")) { … }
///
/// 几个坑：
///   • 正文是只读 TextBox，App.xaml 里 TextBox 的只读触发器会把背景改成 Card，
///     所以外面那层 Border 也用 Card，两层同色才看不出接缝。
///   • WindowStyle=ToolWindow 时没有系统按钮，Esc 必须自己处理（PreviewKeyDown）。
///   • 剪贴板被别的进程占用会抛 COMException，要重试几次，不能直接吞掉。
/// </summary>
public partial class MsgWindow : Window
{
    private readonly string _plainTitle;
    private readonly string _plainBody;

    public MsgWindow(string title, string message, MsgKind kind)
    {
        InitializeComponent();

        _plainTitle = title ?? string.Empty;
        _plainBody = message ?? string.Empty;

        TitleText.Text = _plainTitle;
        BodyText.Text = _plainBody;

        // 用 SetResourceReference 而不是 FindResource：这样色条是 DynamicResource，
        // 窗口已打开时切换主题也能立刻跟着变。
        KindBar.SetResourceReference(BackgroundProperty, KindToBrushKey(kind));

        Title = kind switch
        {
            MsgKind.Error => "LanShare - 错误",
            MsgKind.Warning => "LanShare - 警告",
            _ => "LanShare"
        };

        // 应用当前主题（含 DWM 深色框架 / 圆角）。AttachWindow 内部会挂 SourceInitialized 兜底。
        SourceInitialized += (_, _) => ThemeService.AttachWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective);

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>是否点了"确定"。Confirm 靠它判断。</summary>
    public bool Confirmed { get; private set; }

    private static string KindToBrushKey(MsgKind kind) => kind switch
    {
        MsgKind.Error => "Danger",
        MsgKind.Warning => "Warn",
        _ => "Accent"
    };

    /// <summary>显示一个提示框（模态）。owner 不可见时自动不带 owner，避免弹到屏幕角落。</summary>
    public static void Show(Window? owner, string title, string message, MsgKind kind = MsgKind.Info)
    {
        var w = new MsgWindow(title, message, kind);
        Attach(w, owner);
        w.ShowDialog();
    }

    /// <summary>确认框，返回 true 表示用户点了"确定"。取消/关闭返回 false。</summary>
    public static bool Confirm(Window? owner, string title, string message)
    {
        var w = new MsgWindow(title, message, MsgKind.Info);
        w.CancelButton.Visibility = Visibility.Visible;
        // 有取消时才允许 Esc 关窗；否则 Esc 也当成"确定"以外的关闭，仍然返回 false。
        Attach(w, owner);
        w.ShowDialog();
        return w.Confirmed;
    }

    /// <summary>带自定义按钮文案的确认框（危险操作把"确定"换成"删除"之类）。</summary>
    public static bool Confirm(Window? owner, string title, string message, string okText, MsgKind kind)
    {
        var w = new MsgWindow(title, message, kind);
        w.CancelButton.Visibility = Visibility.Visible;
        w.OkButton.Content = string.IsNullOrWhiteSpace(okText) ? "确定" : okText;
        Attach(w, owner);
        w.ShowDialog();
        return w.Confirmed;
    }

    private static void Attach(MsgWindow w, Window? owner)
    {
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, w))
        {
            w.Owner = owner;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = _plainTitle.Length == 0
            ? _plainBody
            : _plainTitle + Environment.NewLine + Environment.NewLine + _plainBody;

        var copied = await TryCopyAsync(text);
        CopyButton.Content = copied ? "已复制 ✓" : "复制失败";

        // 1.5 秒后恢复按钮文案。用 BeginInvoke 直接挂 timer，窗口提前关掉也不会抛。
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { CopyButton.Content = "复制全部"; } catch { /* 窗口已关闭 */ }
        };
        timer.Start();
    }

    /// <summary>
    /// 剪贴板写入。别的进程正持有剪贴板时会抛 COMException，
    /// 重试 5 次（每次 60ms）大多能成功，比直接报错体验好得多。
    /// </summary>
    private static async Task<bool> TryCopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                await Task.Delay(60);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("MsgWindow.Copy", ex);
                return false;
            }
        }

        return false;
    }
}
