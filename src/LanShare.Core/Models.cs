using System.Text.Json.Serialization;

namespace LanShare.Core;

public static class LanShareConstants
{
    public const string ProductName = "内网快传";
    public const string EnglishName = "LanShare";
    public const string OwnerCookieName = "lanshare_uid";
    public const string OwnerCookiePath = "/";
    public const string DesktopOwnerId = "desktop";
    public const string DataFolderName = "LanShare";
    public const string DefaultArchiveExtension = ".7z";

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
}

public sealed class ShareFile
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
}

/// <summary>A single share record. Persisted in shares.json.</summary>
public sealed class ShareRecord
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DownloadName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool BurnAfterDownload { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsPackaged { get; set; }
    public bool IsUploaded { get; set; }
    public int DownloadCount { get; set; }
    public long TotalBytes { get; set; }
    public string Source { get; set; } = "web";

    /// <summary>Absolute path of the file that /d sends. For a single file this is the original file
    /// (referenced) or the stored copy (uploaded). For a package it is the generated .7z archive.</summary>
    public string? DownloadPath { get; set; }

    /// <summary>Files/directories created by LanShare that must be deleted when the share is destroyed.
    /// Never contains user-owned source files for desktop "reference" shares.</summary>
    public List<string> OwnedPaths { get; set; } = new();

    public List<ShareFile> Files { get; set; } = new();

    [JsonIgnore] public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    [JsonIgnore] public bool IsAvailable => !IsRevoked && !IsExpired;
}

public sealed record UploadedFile(string TempPath, string OriginalName, long Length);

public enum ShareSource
{
    Web,
    Desktop
}

public sealed class ShareFileDto
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
}

public sealed class ShareDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DownloadName { get; set; } = "";
    public long TotalBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool BurnAfterDownload { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public bool IsPackaged { get; set; }
    public bool IsUploaded { get; set; }
    public int DownloadCount { get; set; }
    public string Source { get; set; } = "";
    public string InternalUrl { get; set; } = "";
    public string ExternalUrl { get; set; } = "";
    public List<ShareFileDto> Files { get; set; } = new();
}

public sealed record NetworkAddress(string InterfaceName, string Address, string Description);
