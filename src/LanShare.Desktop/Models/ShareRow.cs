using System.ComponentModel;
using System.Runtime.CompilerServices;
using LanShare.Core;

namespace LanShare.Desktop.Models;

/// <summary>A UI projection of a ShareRecord with live countdown and formatted values.</summary>
public sealed class ShareRow : INotifyPropertyChanged
{
    public ShareRow(ShareRecord record, string internalUrl, string externalUrl)
    {
        Record = record;
        InternalUrl = internalUrl;
        ExternalUrl = externalUrl;
    }

    public ShareRecord Record { get; }
    public string Id => Record.Id;
    public string InternalUrl { get; }
    public string ExternalUrl { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Record.DisplayName) ? "未命名分享" : Record.DisplayName;
    public string SizeText => FormatBytes(Record.TotalBytes);
    public string DownloadCountText => Record.DownloadCount.ToString();
    public string CreatedText => Record.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string SourceText => string.Equals(Record.Source, "desktop", StringComparison.OrdinalIgnoreCase)
        ? "桌面端"
        : string.Equals(Record.Source, "web", StringComparison.OrdinalIgnoreCase) ? "网页上传" : Record.Source;

    public string StatusText
    {
        get
        {
            if (Record.IsRevoked) return "已终止";
            if (Record.IsExpired) return "已过期";
            return "有效";
        }
    }

    public string RemainingText
    {
        get
        {
            if (Record.IsRevoked) return "已终止";
            if (Record.IsExpired) return "已过期";
            return FormatRemaining(Record.ExpiresAt - DateTimeOffset.UtcNow);
        }
    }

    public string ExpiryText => Record.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string FileSummary
    {
        get
        {
            if (Record.Files.Count == 0) return Record.IsPackaged ? "压缩包" : "无文件";
            if (Record.Files.Count == 1) return Record.Files[0].Name;
            return $"{Record.Files.Count} 个文件";
        }
    }

    public bool CanOpen => !string.IsNullOrWhiteSpace(InternalUrl);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Tick()
    {
        Raise(nameof(RemainingText));
        Raise(nameof(StatusText));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatRemaining(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "已过期";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{Math.Max(0, span.Seconds)} 秒";
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
