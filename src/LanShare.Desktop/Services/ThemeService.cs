using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LanShare.Desktop.Services;

/// <summary>
/// 主题切换。设计来源：JavaDeck 的 ThemeManager（那份代码是踩了一堆坑之后稳定下来的）。
///
/// 深色主题的四个必要条件（缺一个就会出现"白底黑字"或"切了没反应"）：
///   1. 两套 ResourceDictionary（Themes/Light.xaml、Themes/Dark.xaml）键完全相同、语义命名
///   2. 所有引用主题色的地方必须用 DynamicResource，StaticResource 不会重新求值
///   3. 切换时必须**原地改同一个 ResourceDictionary 实例的 Source**，
///      把 MergedDictionaries[i] 换成新实例不会触发已绑定元素重新求值
///   4. 已 Show() 过的窗口里，部分元素在 Show 那一刻就求值完毕了，
///      需要遍历视觉树把已知主题背景色重推一遍（ReassertBrushes）
///
/// 另外窗口非客户区（圆角/外框/阴影）由 DWM 绘制，必须显式告诉它深色还是浅色，
/// 见 WindowEffects。
/// </summary>
public static class ThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string System = "System";

    // 必须用绝对 pack URI。相对 URI 在运行时会被解析成 "themes/dark.xaml"（小写、无程序集前缀），
    // PackWebRequest 找不到对应的 baml，静默失败。
    private static readonly Uri LightUri =
        new("pack://application:,,,/LanShare;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkUri =
        new("pack://application:,,,/LanShare;component/Themes/Dark.xaml", UriKind.Absolute);

    private static readonly object Gate = new();
    private static bool _systemHooked;
    private static string _current = Dark;
    private static bool _windowsHooked;

    public static string Normalize(string? theme) => theme?.Trim() switch
    {
        Light => Light,
        System => System,
        _ => Dark
    };

    /// <summary>当前主题名（Dark / Light / System）。</summary>
    public static string Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>实际生效的是不是深色。</summary>
    public static bool IsDarkEffective => Current switch
    {
        Dark => true,
        Light => false,
        _ => SystemPrefersDark(),
    };

    /// <summary>应用暗/亮/跟随系统，并立即刷到全部已打开窗口。</summary>
    public static void Apply(string? theme)
    {
        var normalized = Normalize(theme);

        lock (Gate)
        {
            _current = normalized;
            if (normalized == System)
            {
                if (!_systemHooked)
                {
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                    _systemHooked = true;
                }
            }
            else
            {
                UnhookLocked();
            }
        }

        ApplyCore(IsDarkEffective);
    }

    /// <summary>程序退出时解绑静态订阅。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            UnhookLocked();
            _current = Dark;
        }
    }

    /// <summary>
    /// 让某个窗口跟随当前主题。窗口在 SourceInitialized 之前 HWND 还不存在，
    /// 所以这里既立刻尝试一次，也挂一个 SourceInitialized 兜底。
    /// </summary>
    public static void AttachWindow(Window? window)
    {
        if (window is null) return;

        ApplyToWindow(window, IsDarkEffective);

        if (!_windowsHooked)
        {
            _windowsHooked = true;
            // 全局钩子：任何窗口 Show 之后都重新按当前主题刷一遍，
            // 免得某个对话框在 Show() 时把 DynamicResource 求值成旧主题。
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) =>
                {
                    if (s is Window w) ApplyToWindow(w, IsDarkEffective);
                }));
        }
    }

    /// <summary>对单个窗口应用当前主题（含 DWM 深色框架）。</summary>
    public static void ApplyToWindow(Window? window, bool dark)
    {
        if (window is null) return;

        try
        {
            if (Application.Current?.TryFindResource("Bg") is Brush bg) window.Background = bg;
            if (Application.Current?.TryFindResource("Fg") is Brush fg) window.Foreground = fg;

            ReassertBrushes(window);

            WindowEffects.ApplyRoundedCorners(window, true);
            WindowEffects.SetDarkMode(window, dark);
            // COLORREF 是 0x00BBGGRR：浅色 #E2E5EB -> 0x00EBE5E2；深色 #2E333E -> 0x003E332E
            WindowEffects.SetBorderColor(window, dark ? 0x003E_332E : 0x00EB_E5E2);
            WindowEffects.SetCaptionColor(window, dark ? 0x0010_1715 : 0x00F3_F4F7);
            WindowEffects.SetCaptionTextColor(window, dark ? 0x00F0_EBE8 : 0x0026_1F1C);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ApplyToWindow", ex);
        }
    }

    private static void ApplyCore(bool dark)
    {
        var app = Application.Current;
        if (app?.Resources is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var uri = dark ? DarkUri : LightUri;

        ResourceDictionary? target = null;
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i]?.Source?.OriginalString ?? string.Empty;
            if (src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                target = dicts[i];
                break;
            }
        }

        if (target is null)
        {
            dicts.Insert(0, new ResourceDictionary { Source = uri });
        }
        else if (!UriEquals(target.Source, uri))
        {
            // 关键：换 Source 而不是换实例。
            target.Source = uri;
        }

        foreach (Window w in app.Windows) ApplyToWindow(w, dark);
    }

    private static bool UriEquals(Uri? a, Uri? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.OriginalString, b.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch { return false; }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        lock (Gate)
        {
            if (!_systemHooked || _current != System) return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            lock (Gate)
            {
                if (_current != System) return;
            }
            try { ApplyCore(SystemPrefersDark()); }
            catch (Exception ex) { ErrorLog.Write("SystemThemeChanged", ex); }
        }));
    }

    private static void UnhookLocked()
    {
        if (!_systemHooked) return;
        _systemHooked = false;
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; }
        catch { /* logoff 期间 SystemEvents 可能已经关了 */ }
    }

    /// <summary>
    /// 把主题背景色重新推给视觉树里"用了主题色"的元素。
    /// 为什么需要：DynamicResource 在元素首次创建时求值，字典换 Source 后
    /// 大多数元素会刷新，但 Show() 过程中已求值的少数元素不会。这里按
    /// "当前颜色是否属于已知主题背景色"判断并重设，等价于手动失效一次。
    /// </summary>
    private static void ReassertBrushes(DependencyObject root)
    {
        try
        {
            if (root is System.Windows.Controls.Border border && border.Background is SolidColorBrush bb &&
                IsThemeBackgroundColor(bb.Color))
            {
                if (Application.Current?.TryFindResource("Bg") is Brush nb) border.Background = nb;
            }
            else if (root is System.Windows.Controls.Panel panel && panel.Background is SolidColorBrush pb &&
                     IsThemeBackgroundColor(pb.Color))
            {
                if (Application.Current?.TryFindResource("Bg") is Brush nb) panel.Background = nb;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                ReassertBrushes(VisualTreeHelper.GetChild(root, i));
        }
        catch { }
    }

    private static readonly Color[] KnownBackgrounds =
    {
        Color.FromRgb(0xF3, 0xF4, 0xF7), // 浅色 Bg
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 浅色 Panel
        Color.FromRgb(0xFA, 0xFB, 0xFC), // 浅色 Card
        Color.FromRgb(0x15, 0x17, 0x1C), // 深色 Bg
        Color.FromRgb(0x1B, 0x1E, 0x25), // 深色 Panel
        Color.FromRgb(0x21, 0x25, 0x2E), // 深色 Card
    };

    private static bool IsThemeBackgroundColor(Color c)
    {
        foreach (var known in KnownBackgrounds)
            if (known == c) return true;
        return false;
    }
}
