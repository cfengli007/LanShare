namespace LanShare.Core;

public interface ISettingsService
{
    string DataDirectory { get; }
    string DefaultStorageDirectory { get; }
    AppSettings Current { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    event EventHandler? Changed;
}

public interface IShareService
{
    event EventHandler? Changed;

    Task<IReadOnlyList<ShareRecord>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ShareRecord>> GetByOwnerAsync(string ownerId, CancellationToken ct = default);
    Task<ShareRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Create a share referencing files that live outside the storage directory (desktop right-click / picker).
    /// One file is shared directly; two or more files or a directory are packed into a .7z archive. User files are never deleted.</summary>
    Task<ShareRecord> CreateFromPathsAsync(IReadOnlyList<string> paths, string ownerId, TimeSpan expiry, bool burn, string source, CancellationToken ct = default);

    /// <summary>Create a share from files uploaded to temp storage. The files are moved into a per-share storage folder.
    /// Multiple uploads are packed into a .7z archive; a single upload is served directly.</summary>
    Task<ShareRecord> CreateFromUploadsAsync(IReadOnlyList<UploadedFile> files, string ownerId, TimeSpan expiry, bool burn, string displayName, CancellationToken ct = default);

    /// <summary>Remove the share. requireOwner=true enforces owner identity. Uploaded payloads and generated archives are deleted.</summary>
    Task<bool> RevokeAsync(string id, string? ownerId = null, bool requireOwner = true, CancellationToken ct = default);

    /// <summary>Validate and count a download. Returns null when the share is missing, revoked or expired.</summary>
    Task<ShareRecord?> RegisterDownloadAsync(string id, CancellationToken ct = default);

    /// <summary>Called after the download response has finished. Burn-after-download shares are destroyed here.</summary>
    Task CompleteDownloadAsync(string id, CancellationToken ct = default);

    Task<int> CleanupExpiredAsync(CancellationToken ct = default);

    string GetDownloadName(ShareRecord share);
    string? GetDownloadPath(ShareRecord share);
}

public interface IServerHost
{
    bool IsRunning { get; }
    string? LastError { get; }
    IReadOnlyList<string> Urls { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
    event EventHandler? StateChanged;
}

public interface ILanShareRuntime : IAsyncDisposable
{
    ISettingsService SettingsService { get; }
    IShareService Shares { get; }
    IServerHost Server { get; }
    AppSettings Settings { get; }
    Task ApplySettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task ShutdownAsync();
}

public static class LinkBuilder
{
    public static string InternalBase(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.InternalBaseUrl))
            return settings.InternalBaseUrl.Trim().TrimEnd('/');
        var ip = NetworkInfo.GetPrimaryIPv4();
        return $"http://{ip}:{settings.Port}";
    }

    public static string ExternalBase(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExternalBaseUrl))
            return settings.ExternalBaseUrl.Trim().TrimEnd('/');
        return InternalBase(settings);
    }

    public static string ShareUrl(string baseUrl, string id) => $"{baseUrl.TrimEnd('/')}/s/{id}";

    public static string DownloadUrl(string baseUrl, string id) => $"{baseUrl.TrimEnd('/')}/d/{id}";

    public static ShareDto ToDto(ShareRecord s, AppSettings settings)
    {
        var internalBase = InternalBase(settings);
        var externalBase = ExternalBase(settings);
        return new ShareDto
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
            DownloadName = s.DownloadName,
            TotalBytes = s.TotalBytes,
            CreatedAt = s.CreatedAt,
            ExpiresAt = s.ExpiresAt,
            BurnAfterDownload = s.BurnAfterDownload,
            IsExpired = s.IsExpired,
            IsRevoked = s.IsRevoked,
            IsPackaged = s.IsPackaged,
            IsUploaded = s.IsUploaded,
            DownloadCount = s.DownloadCount,
            Source = s.Source,
            InternalUrl = ShareUrl(internalBase, s.Id),
            ExternalUrl = ShareUrl(externalBase, s.Id),
            Files = s.Files.Select(f => new ShareFileDto { Name = f.Name, Length = f.Length }).ToList()
        };
    }
}
