using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanShare.Desktop.Controls;

public enum BannerSeverity { Info, Success, Warning, Error }

/// <summary>
/// 页面内联提示条（替代 WPF-UI 的 InfoBar）。
///
/// 不用 ui:InfoBar 的原因：整套 WPF-UI 已从项目移除，且它的默认模板在深色下要靠库内部
/// 资源字典才会正确，自绘反而简单可靠。
///
/// 结构：左侧 3px 色条 + 右侧两行（标题 SemiBold / 正文 FgDim）。
/// 配色：Info->AccentSoft+Accent，Success->OkSoft+Ok，Warning->WarnSoft+Warn，Error->DangerSoft+Danger。
/// 用 SetResourceReference 引用主题色，深浅切换无需重建。
/// </summary>
public sealed class InfoBanner : Border
{
    private readonly Border _bar;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private BannerSeverity _severity = BannerSeverity.Info;

    public InfoBanner()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 9, 12, 9);
        Visibility = Visibility.Collapsed;

        _bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 10, 1) };

        _title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _message = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        var textStack = new StackPanel();
        textStack.Children.Add(_title);
        textStack.Children.Add(_message);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_bar, 0);
        Grid.SetColumn(textStack, 1);
        inner.Children.Add(_bar);
        inner.Children.Add(textStack);
        Child = inner;

        ApplySeverity();
    }

    public BannerSeverity Severity
    {
        get => _severity;
        set { _severity = value; ApplySeverity(); }
    }

    public string Title
    {
        get => _title.Text;
        set { _title.Text = value ?? string.Empty; _title.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    public string Message
    {
        get => _message.Text;
        set
        {
            _message.Text = value ?? string.Empty;
            _message.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public bool IsOpen
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>一次性设置并展开。</summary>
    public void Show(BannerSeverity severity, string title, string message = "")
    {
        Severity = severity;
        Title = title;
        Message = message;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    private void ApplySeverity()
    {
        var (background, border, accent) = _severity switch
        {
            BannerSeverity.Success => ("OkSoft", "OkSoft", "Ok"),
            BannerSeverity.Warning => ("WarnSoft", "WarnSoft", "Warn"),
            BannerSeverity.Error => ("DangerSoft", "DangerSoft", "Danger"),
            _ => ("AccentSoft", "AccentSoft", "Accent"),
        };

        SetResourceReference(BackgroundProperty, background);
        SetResourceReference(BorderBrushProperty, border);
        _bar.SetResourceReference(BackgroundProperty, accent);
        _title.SetResourceReference(TextBlock.ForegroundProperty, accent);
    }
}
