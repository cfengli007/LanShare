using System.Windows;
using System.Windows.Controls;

namespace LanShare.Desktop.Pages;

/// <summary>
/// 所有主界面页面的基类。页面只负责"内容区"，标题栏/菜单栏/状态条/按钮栏由 MainWindow 提供。
///
/// 约定（所有页面必须遵守）：
///   • 页面根元素是 ResourceDictionary 之外的 UserControl，内部第一个子元素通常是
///     &lt;ScrollViewer VerticalScrollBarVisibility="Auto"&gt;，左右内边距 18，上下 14。
///   • 页面不要自己画背景色：内容区背景由 MainWindow 的 Card 承载。
///   • 页面不要写死颜色/字号，全部用 App.xaml 里的样式键（PageTitle / PageSubtitle /
///     SectionTitle / FieldLabel / HintText / Card / FlatCard / Badge* / FlatButton /
///     PrimaryButton / DangerButton / SmallButton / SmallPrimaryButton / BadgeButton…）。
///   • 状态条文字用 SetStatus()，不要自己弹对话框。
///   • 轻提示用 Toast()（MainWindow 右下角浮层），不要用 MessageBox。
///   • 需要在底部按钮栏出现的主操作，重写 BuildActions() 返回一个横向 StackPanel。
/// </summary>
public class ShellPage : UserControl
{
    /// <summary>菜单"刷新"或页面被重新激活时调用。默认什么都不做。</summary>
    public virtual void RefreshPage() { }

    /// <summary>底部按钮栏的主操作区。返回 null 表示这一页不需要底部主操作。</summary>
    public virtual FrameworkElement? BuildActions() => null;

    /// <summary>页面被导航到时调用一次（每次切换都会调用）。</summary>
    public virtual void ActivatePage() { }

    /// <summary>更新状态条文字。</summary>
    protected void SetStatus(string text) => StatusRequested?.Invoke(this, text ?? string.Empty);

    /// <summary>右下角轻提示。</summary>
    protected void Toast(string title, string message, bool isError = false, bool isSuccess = false)
        => ToastRequested?.Invoke(this, new ShellToast(title, message, isError, isSuccess));

    public event EventHandler<string>? StatusRequested;
    public event EventHandler<ShellToast>? ToastRequested;
}

public sealed record ShellToast(string Title, string Message, bool IsError = false, bool IsSuccess = false);
