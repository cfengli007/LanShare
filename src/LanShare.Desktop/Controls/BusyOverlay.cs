using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LanShare.Desktop.Controls;

/// <summary>
/// 轻量"转圈"遮罩。只覆盖传入的容器内部。
///
/// 三条硬约束（JavaDeck 踩过，别改）：
///   1. 绝不替换 Window.Content —— 那会把自绘标题栏的关闭/最大化按钮也盖住
///   2. 绝不设 owner.IsEnabled = false —— 那会让整个窗口（含标题栏）点不动
///   3. host 必须是内容区的 Grid，不要传 Window
///
/// 典型用法：
///   var busy = BusyOverlay.Show(ContentGrid, "正在保存设置…");
///   try { await Work(); } finally { busy.Dispose(); }
/// </summary>
public sealed class BusyOverlay : IDisposable
{
    private readonly Panel _host;
    private readonly Border _overlay;
    private bool _disposed;

    private BusyOverlay(Panel host, string message)
    {
        _host = host;

        var ring = new Ellipse
        {
            Width = 22,
            Height = 22,
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 12, 8 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
        };
        ring.SetResourceReference(Shape.StrokeProperty, "Accent");

        var rotate = (RotateTransform)ring.RenderTransform;
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);

        var text = new TextBlock
        {
            Text = message,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(ring);
        stack.Children.Add(text);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 16, 22, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "Panel");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Line");

        _overlay = new Border { Child = bubble };
        _overlay.SetResourceReference(Border.BackgroundProperty, "Overlay");
        Panel.SetZIndex(_overlay, 9000);

        host.Children.Add(_overlay);
    }

    /// <summary>在 host 上叠加一个转圈遮罩。返回的对象 Dispose 时移除。</summary>
    public static BusyOverlay Show(Panel? host, string message = "正在处理…")
    {
        if (host is null) return new BusyOverlay(new Grid(), message);
        return new BusyOverlay(host, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _host.Children.Remove(_overlay); }
        catch { }
    }
}
