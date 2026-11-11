using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LanShare.Desktop.Services;

/// <summary>
/// Windows 11 圆角 / 深色框架 / 边框色 / 标题栏配色。
///
/// 为什么需要这一层：本程序用 WindowStyle=None 自绘标题栏，非客户区（圆角、外框、阴影）
/// 仍由 DWM 绘制。不调用 DwmSetWindowAttribute 的话，深色主题下窗口是直角 + 白色外框，
/// 与自绘内容格格不入。低版本 Windows 会安全地忽略这些属性。
///
/// DWMWA_* 常量含义：
///   20 = USE_IMMERSIVE_DARK_MODE（深色框架，Win10 1809+，注意 19 是旧写法）
///   33 = WINDOW_CORNER_PREFERENCE（Win11 22000+，2 = Round）
///   34 = BORDER_COLOR（Win11 22000+，COLORREF 0x00BBGGRR，0xFFFFFFFE = 系统默认）
///   35 = CAPTION_COLOR（Win11 22000+）
///   36 = TEXT_COLOR（Win11 22000+）
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private enum DwmWindowCornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static IntPtr HandleOf(Window window)
    {
        try { return new WindowInteropHelper(window).Handle; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>应用 Windows 11 圆角。低版本系统会返回错误码，忽略即可。</summary>
    public static void ApplyRoundedCorners(Window? window, bool round = true)
    {
        if (window is null) return;
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var pref = (int)(round ? DwmWindowCornerPreference.Round : DwmWindowCornerPreference.DoNotRound);
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>深色框架（覆盖非客户区、圆角外框、阴影）。</summary>
    public static void SetDarkMode(Window? window, bool dark)
    {
        if (window is null) return;
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        catch { }
    }

    /// <summary>窗口边框颜色。COLORREF 是 0x00BBGGRR（注意与 #RRGGBB 字节序相反）。</summary>
    public static void SetBorderColor(Window? window, int colorRef)
    {
        if (window is null) return;
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int)); }
        catch { }
    }

    /// <summary>标题栏底色（Win11 22000+）。我们用自绘标题栏，设了也无害。</summary>
    public static void SetCaptionColor(Window? window, int colorRef)
    {
        if (window is null) return;
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        try { DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int)); }
        catch { }
    }

    /// <summary>标题栏文字颜色（Win11 22000+）。</summary>
    public static void SetCaptionTextColor(Window? window, int colorRef)
    {
        if (window is null) return;
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        try { DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref colorRef, sizeof(int)); }
        catch { }
    }
}
