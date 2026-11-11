using System.Runtime.InteropServices;
using System.Text.Json;
using LanShare.Core.Infrastructure;

namespace LanShare.Core;

/// <summary>Raised when an upload exceeds <see cref="AppSettings.MaxUploadBytes"/>.</summary>
public sealed class ShareTooLargeException : Exception
{
    public long LimitBytes { get; }
    public long ActualBytes { get; }

    public ShareTooLargeException(long limitBytes, long actualBytes)
        : base($"上传内容过大：{actualBytes} 字节，超过上限 {limitBytes} 字节。")
    {
        LimitBytes = limitBytes;
        ActualBytes = actualBytes;
    }
}

/// <summary>Persistent share store. All mutations are serialized; callers always receive clones.</summary>
public sealed class ShareService : IShareService
{
    private const string SharesFileName = "shares.json";
    private static readonly TimeSpan MinExpiry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxExpiry = TimeSpan.FromMinutes(43200);

    private readonly ISettingsService _settings;
    private readonly SevenZipTool _sevenZip;
    private readonly FileLogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ShareRecord> _shares = new(StringComparer.Ordinal);
    private readonly string _dataDirectory;
    private readonly string _sharesPath;

    public ShareService(ISettingsService settings, SevenZipTool sevenZip, FileLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sevenZip = sevenZip ?? throw new ArgumentNullException(nameof(sevenZip));
        _log = logger;
        _dataDirectory = Path.GetFullPath(settings.DataDirectory);
        _sharesPath = Path.Combine(_dataDirectory, SharesFileName);
        Directory.CreateDirectory(_dataDirectory);
        Load();
        CleanupTempFiles(TimeSpan.FromDays(1));
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<ShareRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _shares.Values
                .Where(s => !s.IsRevoked)
                .OrderByDescending(s => s.CreatedAt)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShareRecord>> GetByOwnerAsync(string ownerId, CancellationToken ct = default)
    {
        var owner = ownerId ?? "";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _shares.Values
                .Where(s => !s.IsRevoked && string.Equals(s.OwnerId, owner, StringComparison.Ordinal))
                .OrderByDescending(s => s.CreatedAt)
                .Select(Clone)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _shares.TryGetValue(id, out var record) ? Clone(record) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareRecord> CreateFromPathsAsync(IReadOnlyList<string> paths, string ownerId, TimeSpan expiry, bool burn, string source, CancellationToken ct = default)
    {
        if (paths == null || paths.Count == 0)
            throw new ArgumentException("没有选择要分享的文件。", nameof(paths));

        var normalized = NormalizeSourcePaths(paths);
        if (normalized.Count == 0)
            throw new ArgumentException("没有找到可分享的文件或文件夹。", nameof(paths));

        var id = RandomToken.UrlSafe(16);
        var now = DateTimeOffset.UtcNow;
        var expires = now + NormalizeExpiry(expiry);
        var storageRoot = GetStorageRoot();
        var shareDir = Path.Combine(storageRoot, id);
        var owned = new List<string> { shareDir };
        var files = new List<ShareFile>();
        string display;
        string downloadName;
        string downloadPath;
        bool packaged;
        long totalBytes = 0;

        try
        {
            if (normalized.Count == 1 && File.Exists(normalized[0]))
            {
                var full = normalized[0];
                var info = new FileInfo(full);
                display = SafePaths.SanitizeFileName(Path.GetFileName(full), "file");
                downloadName = display;
                downloadPath = full;
                packaged = false;
                totalBytes = info.Length;
                files.Add(new ShareFile { Name = display, Length = info.Length });
                owned.Clear(); // reference share: never delete the user's file
                Directory.CreateDirectory(shareDir);
            }
            else
            {
                Directory.CreateDirectory(shareDir);
                var packDir = Path.Combine(shareDir, ".pack");
                Directory.CreateDirectory(packDir);
                var usedTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var topNames = new List<string>();

                foreach (var full in normalized)
                {
                    var leaf = SafePaths.SanitizeFileName(GetLeafName(full), "item");
                    var topName = SafePaths.MakeUnique(leaf, usedTopNames);
                    if (Directory.Exists(full))
                    {
                        var destination = Path.Combine(packDir, topName);
                        LinkDirectoryTree(full, destination, 0);
                        var size = SafePaths.GetPathSize(full, _log);
                        files.Add(new ShareFile { Name = leaf, Length = size });
                        totalBytes += size;
                    }
                    else
                    {
                        var destination = Path.Combine(packDir, topName);
                        HardLinkOrCopy(full, destination);
                        var size = new FileInfo(destination).Length;
                        files.Add(new ShareFile { Name = leaf, Length = size });
                        totalBytes += size;
                    }

                    topNames.Add(topName);
                }

                display = BuildMultiDisplay(normalized);
                var archiveName = BuildArchiveName(display);
                downloadPath = Path.Combine(shareDir, archiveName);
                downloadName = archiveName;
                packaged = true;

                _log?.Info($"正在打包分享 {id}（{topNames.Count} 项，{totalBytes} 字节）");
                await _sevenZip.AddAsync(downloadPath, packDir, topNames, ct).ConfigureAwait(false);
                SafePaths.TryDeleteDirectory(packDir, _log);
            }
        }
        catch
        {
            foreach (var path in owned) SafePaths.TryDeleteDirectory(path, _log);
            SafePaths.TryDeleteDirectory(shareDir, _log);
            throw;
        }

        var record = new ShareRecord
        {
            Id = id,
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "anonymous" : ownerId.Trim(),
            DisplayName = display,
            DownloadName = downloadName,
            CreatedAt = now,
            ExpiresAt = expires,
            BurnAfterDownload = burn,
            IsRevoked = false,
            IsPackaged = packaged,
            IsUploaded = false,
            DownloadCount = 0,
            TotalBytes = totalBytes,
            Source = string.IsNullOrWhiteSpace(source) ? "web" : source.Trim(),
            DownloadPath = downloadPath,
            OwnedPaths = owned,
            Files = files
        };

        await AddAndSaveAsync(record, ct).ConfigureAwait(false);
        RaiseChanged();
        return Clone(record);
    }

    public async Task<ShareRecord> CreateFromUploadsAsync(IReadOnlyList<UploadedFile> files, string ownerId, TimeSpan expiry, bool burn, string displayName, CancellationToken ct = default)
    {
        if (files == null || files.Count == 0)
            throw new ArgumentException("没有收到上传的文件。", nameof(files));

        var settings = _settings.Current;
        var maxBytes = settings.MaxUploadBytes;
        long totalBytes = 0;
        foreach (var file in files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.TempPath) || !File.Exists(file.TempPath))
                throw new InvalidOperationException("上传的临时文件不存在或已被清理。");
            totalBytes += new FileInfo(file.TempPath).Length;
        }

        if (maxBytes > 0 && totalBytes > maxBytes)
            throw new ShareTooLargeException(maxBytes, totalBytes);

        var id = RandomToken.UrlSafe(16);
        var now = DateTimeOffset.UtcNow;
        var expires = now + NormalizeExpiry(expiry);
        var storageRoot = GetStorageRoot();
        var shareDir = Path.Combine(storageRoot, id);
        Directory.CreateDirectory(shareDir);

        var recordFiles = new List<ShareFile>();
        string display;
        string downloadName;
        string downloadPath;
        bool packaged;
        long storedBytes = 0;

        try
        {
            if (files.Count == 1)
            {
                var file = files[0];
                var safeName = SafePaths.SanitizeFileName(file.OriginalName, "file");
                var destination = Path.Combine(shareDir, safeName);
                await MoveFileAsync(file.TempPath, destination, ct).ConfigureAwait(false);
                var length = new FileInfo(destination).Length;
                storedBytes = length;
                display = string.IsNullOrWhiteSpace(displayName) ? safeName : CleanDisplayName(displayName, safeName);
                downloadName = safeName;
                downloadPath = destination;
                packaged = false;
                recordFiles.Add(new ShareFile { Name = safeName, Length = length });
            }
            else
            {
                var packDir = Path.Combine(shareDir, ".pack");
                Directory.CreateDirectory(packDir);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var safeName = SafePaths.MakeUnique(SafePaths.SanitizeFileName(file.OriginalName, "file"), used);
                    var destination = Path.Combine(packDir, safeName);
                    await MoveFileAsync(file.TempPath, destination, ct).ConfigureAwait(false);
                    var length = new FileInfo(destination).Length;
                    storedBytes += length;
                    recordFiles.Add(new ShareFile { Name = safeName, Length = length });
                }

                display = string.IsNullOrWhiteSpace(displayName)
                    ? BuildMultiDisplay(recordFiles.Select(f => f.Name).ToList())
                    : CleanDisplayName(displayName, "分享文件");
                var archiveName = BuildArchiveName(display);
                downloadPath = Path.Combine(shareDir, archiveName);
                downloadName = archiveName;
                packaged = true;

                _log?.Info($"正在打包上传分享 {id}（{recordFiles.Count} 个文件，{storedBytes} 字节）");
                await _sevenZip.AddAsync(downloadPath, packDir, recordFiles.Select(f => f.Name).ToList(), ct).ConfigureAwait(false);
                SafePaths.TryDeleteDirectory(packDir, _log);
            }
        }
        catch
        {
            foreach (var file in files)
            {
                try { if (File.Exists(file.TempPath)) File.Delete(file.TempPath); } catch { /* ignore */ }
            }
            SafePaths.TryDeleteDirectory(shareDir, _log);
            throw;
        }

        var record = new ShareRecord
        {
            Id = id,
            OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "anonymous" : ownerId.Trim(),
            DisplayName = display,
            DownloadName = downloadName,
            CreatedAt = now,
            ExpiresAt = expires,
            BurnAfterDownload = burn,
            IsRevoked = false,
            IsPackaged = packaged,
            IsUploaded = true,
            DownloadCount = 0,
            TotalBytes = storedBytes,
            Source = "web",
            DownloadPath = downloadPath,
            OwnedPaths = new List<string> { shareDir },
            Files = recordFiles
        };

        await AddAndSaveAsync(record, ct).ConfigureAwait(false);
        RaiseChanged();
        return Clone(record);
    }

    public async Task<bool> RevokeAsync(string id, string? ownerId = null, bool requireOwner = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        List<string> toDelete = new();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shares.TryGetValue(id, out var record)) return false;
            if (requireOwner && !string.Equals(record.OwnerId, ownerId ?? "", StringComparison.Ordinal))
            {
                _log?.Warn($"拒绝撤销分享 {id}：所有者不匹配（请求 {ownerId ?? "<空>"}，实际 {record.OwnerId}）。");
                return false;
            }

            if (record.IsRevoked) return false;

            record.IsRevoked = true;
            toDelete.AddRange(record.OwnedPaths);
            record.OwnedPaths.Clear();
            record.DownloadPath = null;
            await SaveLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var path in toDelete) DeleteOwnedPath(path);
        _log?.Info($"分享已撤销：{id}");
        RaiseChanged();
        return true;
    }

    public async Task<ShareRecord?> RegisterDownloadAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shares.TryGetValue(id, out var record)) return null;
            if (!record.IsAvailable) return null;
            if (GetExistingDownloadPath(record) == null)
            {
                _log?.Warn($"分享 {id} 的下载文件已不存在：{record.DownloadPath}");
                return null;
            }

            record.DownloadCount++;
            await SaveLockedAsync(ct).ConfigureAwait(false);
            return Clone(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteDownloadAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        List<string> toDelete = new();
        var destroyed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shares.TryGetValue(id, out var record)) return;
            if (!record.BurnAfterDownload || record.IsRevoked) return;

            record.IsRevoked = true;
            toDelete.AddRange(record.OwnedPaths);
            record.OwnedPaths.Clear();
            record.DownloadPath = null;
            await SaveLockedAsync(ct).ConfigureAwait(false);
            destroyed = true;
        }
        finally
        {
            _gate.Release();
        }

        if (!destroyed) return;
        foreach (var path in toDelete) DeleteOwnedPath(path);
        _log?.Info($"阅后即焚分享已销毁：{id}");
        RaiseChanged();
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var cleaned = 0;
        List<string> toDelete = new();
        var changed = false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var record in _shares.Values)
            {
                if (!record.IsExpired || record.IsRevoked) continue;
                if (record.OwnedPaths.Count == 0) continue;

                record.IsRevoked = true;
                toDelete.AddRange(record.OwnedPaths);
                record.OwnedPaths.Clear();
                record.DownloadPath = null;
                cleaned++;
                changed = true;
            }

            if (changed) await SaveLockedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"清理过期分享失败：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        foreach (var path in toDelete) DeleteOwnedPath(path);
        CleanupTempFiles(TimeSpan.FromDays(1));
        if (cleaned > 0)
        {
            _log?.Info($"已清理 {cleaned} 个过期分享的存储内容。");
            RaiseChanged();
        }

        return cleaned;
    }

    public string GetDownloadName(ShareRecord share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (!string.IsNullOrWhiteSpace(share.DownloadName)) return share.DownloadName;
        if (!string.IsNullOrWhiteSpace(share.DownloadPath)) return Path.GetFileName(share.DownloadPath);
        return string.IsNullOrWhiteSpace(share.DisplayName) ? "download" : share.DisplayName;
    }

    public string? GetDownloadPath(ShareRecord share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return GetExistingDownloadPath(share);
    }

    private static string? GetExistingDownloadPath(ShareRecord share)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(share.DownloadPath)) return null;
            var full = Path.GetFullPath(share.DownloadPath);
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task AddAndSaveAsync(ShareRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _shares[record.Id] = record;
            try
            {
                await SaveLockedAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _shares.Remove(record.Id);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveLockedAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = _shares.Values.OrderByDescending(s => s.CreatedAt).ToList();
            await JsonFile.WriteAtomicAsync(_sharesPath, snapshot, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Error($"保存分享记录失败：{_sharesPath}", ex);
            throw new InvalidOperationException($"保存分享记录失败：{ex.Message}", ex);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_sharesPath)) return;
            var json = File.ReadAllText(_sharesPath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var list = JsonSerializer.Deserialize<List<ShareRecord>>(json, JsonFile.ReadOptions);
            if (list == null) return;

            foreach (var record in list)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Id)) continue;
                record.OwnedPaths ??= new List<string>();
                record.Files ??= new List<ShareFile>();
                if (string.IsNullOrWhiteSpace(record.OwnerId)) record.OwnerId = "anonymous";
                if (record.ExpiresAt == default) record.ExpiresAt = record.CreatedAt.AddDays(1);
                _shares[record.Id] = record;
            }

            _log?.Info($"已加载 {_shares.Count} 个分享记录：{_sharesPath}");
        }
        catch (Exception ex)
        {
            _log?.Error($"读取分享记录失败，将使用空列表：{_sharesPath}", ex);
            _shares.Clear();
            try
            {
                if (File.Exists(_sharesPath))
                {
                    var backup = $"{_sharesPath}.broken-{DateTime.Now:yyyyMMddHHmmss}";
                    File.Move(_sharesPath, backup, overwrite: true);
                    _log?.Info($"损坏的分享记录已备份为：{backup}");
                }
            }
            catch (Exception backupError)
            {
                _log?.Warn($"备份损坏的分享记录失败：{backupError.Message}");
            }
        }
    }

    private string GetStorageRoot()
    {
        var settings = _settings.Current;
        var root = SettingsService.ResolveStorageDirectory(settings, _dataDirectory);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法使用存储目录“{root}”：{ex.Message}", ex);
        }

        return root;
    }

    private static TimeSpan NormalizeExpiry(TimeSpan expiry)
    {
        if (expiry <= TimeSpan.Zero) return MinExpiry;
        if (expiry < MinExpiry) return MinExpiry;
        if (expiry > MaxExpiry) return MaxExpiry;
        return expiry;
    }

    private static List<string> NormalizeSourcePaths(IReadOnlyList<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw.Trim()); }
            catch (Exception ex) { throw new ArgumentException($"无效的路径“{raw}”：{ex.Message}", nameof(paths)); }

            if (!File.Exists(full) && !Directory.Exists(full))
                throw new FileNotFoundException($"要分享的文件或文件夹不存在：{full}", full);
            if (SafePaths.IsRootPath(full))
                throw new ArgumentException("不能分享磁盘根目录。", nameof(paths));
            if (seen.Add(full)) result.Add(full);
        }

        return result;
    }

    private static string GetLeafName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? "item" : leaf;
    }

    private static string BuildMultiDisplay(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "分享文件";
        var first = SafePaths.SanitizeFileName(names[0], "文件");
        return names.Count == 1 ? first : $"{first} 等 {names.Count} 项";
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return fallback;
        var cleaned = new string(displayName.Trim().Where(ch => ch >= ' ' && ch != '\0').ToArray()).Trim();
        if (cleaned.Length == 0) return fallback;
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string BuildArchiveName(string display)
    {
        var safe = SafePaths.SanitizeFileName(display, "分享文件");
        return safe.EndsWith(LanShareConstants.DefaultArchiveExtension, StringComparison.OrdinalIgnoreCase)
            ? safe
            : safe + LanShareConstants.DefaultArchiveExtension;
    }

    private static ShareRecord Clone(ShareRecord source) => new()
    {
        Id = source.Id,
        OwnerId = source.OwnerId,
        DisplayName = source.DisplayName,
        DownloadName = source.DownloadName,
        CreatedAt = source.CreatedAt,
        ExpiresAt = source.ExpiresAt,
        BurnAfterDownload = source.BurnAfterDownload,
        IsRevoked = source.IsRevoked,
        IsPackaged = source.IsPackaged,
        IsUploaded = source.IsUploaded,
        DownloadCount = source.DownloadCount,
        TotalBytes = source.TotalBytes,
        Source = source.Source,
        DownloadPath = source.DownloadPath,
        OwnedPaths = new List<string>(source.OwnedPaths),
        Files = source.Files.Select(f => new ShareFile { Name = f.Name, Length = f.Length }).ToList()
    };

    private void DeleteOwnedPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            if (SafePaths.IsRootPath(full)) return;
            if (Directory.Exists(full)) SafePaths.TryDeleteDirectory(full, _log);
            else if (File.Exists(full)) SafePaths.TryDeleteFile(full, _log);
        }
        catch (Exception ex)
        {
            _log?.Warn($"删除分享存储内容失败：{path}：{ex.Message}");
        }
    }

    private void CleanupTempFiles(TimeSpan olderThan)
    {
        try
        {
            var tempDirectory = Path.Combine(GetStorageRoot(), ".tmp");
            if (!Directory.Exists(tempDirectory)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(tempDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"清理上传临时文件失败：{file}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"清理上传临时目录失败：{ex.Message}");
        }
    }

    private static async Task MoveFileAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            File.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Different volume: copy then delete.
        }

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        try { File.Delete(source); } catch { /* best effort */ }
    }

    private void LinkDirectoryTree(string sourceDirectory, string destinationDirectory, int depth)
    {
        if (depth > 64) throw new IOException($"目录层级过深，已停止打包：{sourceDirectory}");
        Directory.CreateDirectory(destinationDirectory);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory, "*", options))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            var destination = Path.Combine(destinationDirectory, name);

            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (Exception ex)
            {
                _log?.Warn($"跳过无法读取的条目：{entry}：{ex.Message}");
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                _log?.Warn($"打包时跳过符号链接/联接点：{entry}");
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
                LinkDirectoryTree(entry, destination, depth + 1);
            else
                HardLinkOrCopy(entry, destination);
        }
    }

    private void HardLinkOrCopy(string source, string destination)
    {
        try
        {
            if (TryCreateHardLink(destination, source)) return;
        }
        catch (Exception ex)
        {
            _log?.Warn($"创建硬链接失败，将复制文件：{source}：{ex.Message}");
        }

        File.Copy(source, destination, overwrite: false);
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero);

            return LinkUnix(existingPath, linkPath) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int LinkUnix(string oldpath, string newpath);

    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _log?.Warn($"分享变更事件处理失败：{ex.Message}"); }
    }
}
