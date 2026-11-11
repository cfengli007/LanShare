using System.Text.Json.Serialization;

namespace LanShare.Core;

/// <summary>User-editable application settings. Persisted as JSON next to the data directory.</summary>
public sealed class AppSettings
{
    public const int DefaultPort = 8910;

    /// <summary>IP to listen on. "0.0.0.0" means all interfaces.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = DefaultPort;

    /// <summary>Optional internal base URL shown for LAN links, e.g. http://192.168.1.10:8910 or http://files.lan.
    /// Empty means auto-detect http://&lt;primary IPv4&gt;:&lt;port&gt;.</summary>
    public string InternalBaseUrl { get; set; } = "";

    /// <summary>Optional public base URL, e.g. https://share.example.com (Nginx terminates TLS).
    /// Empty means the internal URL is reused.</summary>
    public string ExternalBaseUrl { get; set; } = "";

    public int DefaultExpiryMinutes { get; set; } = 1440;

    public bool BurnAfterDownloadByDefault { get; set; }

    /// <summary>Maximum total upload size for one web share, in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Where uploaded files and generated archives are stored. Empty means %LOCALAPPDATA%\LanShare\storage.</summary>
    public string StorageDirectory { get; set; } = "";

    public bool LaunchAtStartup { get; set; }

    public bool NotifyOnShareCreated { get; set; } = true;

    /// <summary>Windows Explorer context-menu integration installed for the current user.</summary>
    public bool ContextMenuInstalled { get; set; }

    /// <summary>Desktop colour theme: "Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "Dark";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public AppSettings Normalize()
    {
        var c = Clone();
        if (string.IsNullOrWhiteSpace(c.BindAddress)) c.BindAddress = "0.0.0.0";
        if (c.Port is < 1 or > 65535) c.Port = DefaultPort;
        c.InternalBaseUrl = (c.InternalBaseUrl ?? "").Trim().TrimEnd('/');
        c.ExternalBaseUrl = (c.ExternalBaseUrl ?? "").Trim().TrimEnd('/');
        if (c.DefaultExpiryMinutes < 1) c.DefaultExpiryMinutes = 1440;
        if (c.DefaultExpiryMinutes > 60 * 24 * 30) c.DefaultExpiryMinutes = 60 * 24 * 30;
        if (c.MaxUploadBytes < 1024 * 1024) c.MaxUploadBytes = 1024 * 1024;
        if (string.IsNullOrWhiteSpace(c.StorageDirectory)) c.StorageDirectory = "";
        c.Theme = (c.Theme ?? "").Trim();
        if (c.Theme is not ("Dark" or "Light" or "System")) c.Theme = "Dark";
        return c;
    }
}
