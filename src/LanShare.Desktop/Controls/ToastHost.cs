using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LanShare.Desktop.Controls;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// 右下角轻提示（替代 WPF-UI 的 SnackbarService）。
///
/// 设计要点：
///   • 自己就是叠加层容器，放进内容区 Grid 即可；同时最多显示 3 条，多了顶掉最旧的
///   • Info/Success 4 秒自动消失，Warning/Error 7 秒（错误要留够阅读时间）
///   • 颜色走 App.xaml 的 Panel/Line/Fg/Ok/Warn/Danger，深浅主题自动跟随
///   • 淡入淡出用 DoubleAnimation，不用 Visibility 硬切（免得闪）
///   • 用 SetResourceReference 而不是 FindResource：主题切换时颜色能自动跟着变
/// </summary>
public sealed class ToastHost : Grid
{
    private const int MaxToasts = 3;
    private readonly StackPanel _stack;

    public ToastHost()
    {
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, 20, 18);
        IsHitTestVisible = true;

        _stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        Children.Add(_stack);
    }

    public void Show(string title, string message, ToastKind kind = ToastKind.Info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Show(title, message, kind)));
            return;
        }

        var accentKey = kind switch
        {
            ToastKind.Success => "Ok",
            ToastKind.Warning => "Warn",
            ToastKind.Error => "Danger",
            _ => "Accent",
        };

        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
        bar.SetResourceReference(Border.BackgroundProperty, accentKey);

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        var body = new TextBlock
        {
            Text = message,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            MaxWidth = 320,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        if (string.IsNullOrWhiteSpace(message)) body.Visibility = Visibility.Collapsed;

        var textStack = new StackPanel();
        textStack.Children.Add(titleBlock);
        textStack.Children.Add(body);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(textStack, 1);
        inner.Children.Add(bar);
        inner.Children.Add(textStack);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 14, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = inner,
            Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.22,
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Popover");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");

        _stack.Children.Add(card);
        while (_stack.Children.Count > MaxToasts) _stack.Children.RemoveAt(0);

        card.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        var lifetime = kind is ToastKind.Warning or ToastKind.Error ? 7.0 : 4.0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(lifetime) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => _stack.Children.Remove(card);
            card.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        timer.Start();
    }
}
