using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using LanShare.Desktop.Services;

namespace LanShare.Desktop.Dialogs;

/// <summary>
/// 关于窗口。骨架：自绘标题栏 40 / 内容 * / 按钮栏 64。
///
/// 版本号从程序集读，绝不写死：
///   优先 AssemblyInformationalVersion（Directory.Build.props 里的 Version 会进这里，
///   而且能带 "+<sha>" 后缀，要去掉），回退 AssemblyVersion。
///   同一个版本号也是自更新比较用的基准（SelfUpdater.CurrentVersion），两边必须一致。
///
/// 项目地址常量写在本文件里是刻意的：它不依赖 Services/Update 是否已经交付，
/// 避免任务 5 没落地时这个窗口编译不过。
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>项目主页。与 README / 自更新用的是同一个仓库。</summary>
    public const string ProjectUrl = "https://github.com/cfengli007/LanShare";

    public AboutWindow()
    {
        InitializeComponent();

        var version = ReadVersion();
        VersionText.Text = $"版本 {version} · 把文件拖进来，任何设备用浏览器就能取";
        RepoText.Text = ProjectUrl;
        CopyrightText.Text = $"Copyright (C) 2026 LanShare Contributors";

        SourceInitialized += (_, _) => ThemeService.AttachWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToWindow(this, ThemeService.IsDarkEffective);

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 读程序集版本。AssemblyInformationalVersion 可能形如 "1.1.0+3f2a1c9"，
    /// "+" 之后是源码提交号，对用户没意义，要去掉。
    /// </summary>
    private static string ReadVersion()
    {
        try
        {
            var asm = typeof(AboutWindow).Assembly;

            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                var trimmed = (plus > 0 ? informational[..plus] : informational).Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            var v = asm.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AboutWindow.ReadVersion", ex);
            return "0.0.0";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        // 标题栏上如果有按钮，点在按钮上会走到按钮自己的命中测试，
        // 这里再做一次 e.OriginalSource 判断，避免 DragMove 抢走捕获。
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject src && IsInButton(src)) return;

        try { DragMove(); } catch { /* 拖动期间窗口可能已被关闭 */ }
    }

    private static bool IsInButton(DependencyObject? node)
    {
        while (node is not null and not Window)
        {
            if (node is System.Windows.Controls.Button) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenRepo(object sender, MouseButtonEventArgs e) => OpenUrl(ProjectUrl);

    private void OnOpenRepoClick(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    /// <summary>
    /// 优先用本地 LICENSE（便携版 / 安装版都在程序目录），
    /// 没有就回落到仓库里的 LICENSE 页面——不要弹"文件不存在"。
    /// </summary>
    private void OnViewLicense(object sender, RoutedEventArgs e)
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            if (File.Exists(local))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + local + "\"") { UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AboutWindow.OnViewLicense", ex);
        }

        OpenUrl(ProjectUrl + "/blob/main/LICENSE");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AboutWindow.OpenUrl", ex);
        }
    }
}
