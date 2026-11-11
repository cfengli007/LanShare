using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LanShare.Core;
using LanShare.Core.Infrastructure;

namespace LanShare.Server.Hosting;

/// <summary>An API failure that carries the public error code and optional resumable-upload progress.</summary>
internal sealed class UploadApiException : Exception
{
    public UploadApiException(int statusCode, string code, string message, long? received = null, long? total = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Received = received;
        Total = total;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public long? Received { get; }
    public long? Total { get; }
}

internal sealed record UploadCreateFile(string Name, long Length, long? LastModified);

internal sealed record UploadFileView(int Index, string Name, long Length, long Received);

internal sealed record UploadSessionView(string UploadId, int ChunkSize, DateTimeOffset ExpiresAt,
    IReadOnlyList<UploadFileView> Files);

internal sealed record UploadPutView(int Index, long Received, long Length, bool Complete);

/// <summary>
/// Resumable chunked upload sessions. Metadata lives in
/// <c>&lt;storage&gt;/.tmp/uploads/&lt;uploadId&gt;/manifest.json</c> and the bytes of file
/// <c>index</c> live in <c>&lt;index&gt;.part</c>. Every operation on one session is serialized.
/// </summary>
internal sealed class UploadSessionService
{
    public const int ChunkSize = 8 * 1024 * 1024;
    public const int MaxFiles = 200;
    private static readonly TimeSpan OrphanRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan StagingRetention = TimeSpan.FromHours(1);

    private readonly ISettingsService _settings;
    private readonly IShareService _shares;
    private readonly FileLogger _log;
    private readonly object _lockGate = new();
    private readonly Dictionary<string, LockEntry> _locks = new(StringComparer.Ordinal);

    public UploadSessionService(ISettingsService settings, IShareService shares, FileLogger log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<UploadSessionView> CreateAsync(IReadOnlyList<UploadCreateFile> files, int? expiryMinutes,
        bool? burn, string? title, string ownerId, CancellationToken ct = default)
    {
        if (files == null) throw new UploadApiException(400, "invalid_request", "请求格式无效：缺少 files 字段。");
        if (files.Count is < 1 or > MaxFiles)
            throw new UploadApiException(400, "invalid_request", $"一次最多上传 {MaxFiles} 个文件，且至少需要 1 个文件。");
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new UploadApiException(400, "invalid_request", "无法识别上传者，请刷新页面后重试。");

        var settings = _settings.Current;
        var minutes = expiryMinutes ?? settings.DefaultExpiryMinutes;
        if (minutes is < 1 or > 43200)
            throw new UploadApiException(400, "invalid_request", "有效期必须是 1 到 43200 分钟之间的整数。");

        var burnValue = burn ?? settings.BurnAfterDownloadByDefault;
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (cleanTitle is { Length: > 120 })
            throw new UploadApiException(400, "invalid_request", "分享标题不能超过 120 个字符。");

        long total = 0;
        var manifestFiles = new List<UploadManifestFile>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index] ?? throw new UploadApiException(400, "invalid_request", "文件信息不完整。");
            var name = UploadReader.NormalizeUploadedName(file.Name);
            if (name.Length == 0)
                throw new UploadApiException(400, "invalid_request", "文件名不能为空。");
            if (file.Length < 0)
                throw new UploadApiException(400, "invalid_request", $"文件“{name}”的大小无效。");
            if (file.Length > long.MaxValue - total)
                throw new UploadApiException(400, "invalid_request", "文件总大小超出支持范围。");

            total += file.Length;
            manifestFiles.Add(new UploadManifestFile
            {
                Index = index,
                Name = name,
                Length = file.Length,
                LastModified = file.LastModified
            });
        }

        if (total <= 0)
            throw new UploadApiException(400, "invalid_request", "文件总大小必须大于 0 字节。");

        var maxBytes = Math.Max(0, settings.MaxUploadBytes);
        if (maxBytes > 0 && total > maxBytes)
            throw new UploadApiException(413, "too_large", $"上传内容过大：共 {total} 字节，超过上限 {maxBytes} 字节。");

        var now = DateTimeOffset.UtcNow;
        var manifest = new UploadManifest
        {
            UploadId = "",
            OwnerId = ownerId.Trim(),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(minutes),
            ExpiryMinutes = minutes,
            Burn = burnValue,
            Title = cleanTitle,
            Files = manifestFiles
        };

        var root = GetUploadsRoot();
        Directory.CreateDirectory(root);

        string id;
        string directory;
        for (var attempt = 0; ; attempt++)
        {
            id = RandomToken.UrlSafe(16);
            directory = Path.Combine(root, id);
            if (Directory.Exists(directory)) continue;
            Directory.CreateDirectory(directory);
            break;
        }

        manifest.UploadId = id;
        try
        {
            await JsonFile.WriteAtomicAsync(ManifestPath(directory), manifest, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }

        _log.Info($"已创建分片上传会话 {id}：{manifestFiles.Count} 个文件，共 {total} 字节。");
        return BuildView(manifest, directory);
    }

    public async Task<UploadSessionView?> GetAsync(string uploadId, string ownerId, CancellationToken ct = default)
    {
        if (!RandomToken.IsValidId(uploadId)) return null;
        using var lease = await EnterAsync(uploadId, ct).ConfigureAwait(false);
        var session = await ReadSessionLockedAsync(uploadId, ownerId, ct).ConfigureAwait(false);
        return session == null ? null : BuildView(session.Manifest, session.Directory);
    }

    public async Task<UploadPutView> AppendAsync(string uploadId, string ownerId, int index, long offset,
        long contentLength, Stream body, CancellationToken ct = default)
    {
        if (!RandomToken.IsValidId(uploadId))
            throw new UploadApiException(404, "upload_not_found", "上传会话不存在或已过期。");
        if (contentLength is < 1 or > ChunkSize)
            throw new UploadApiException(400, "invalid_request", $"每个分片必须为 1 到 {ChunkSize} 字节。");

        using var lease = await EnterAsync(uploadId, ct).ConfigureAwait(false);
        var session = await ReadSessionLockedAsync(uploadId, ownerId, ct).ConfigureAwait(false);
        if (session == null)
            throw new UploadApiException(404, "upload_not_found", "上传会话不存在或已过期。");

        var file = session.Manifest.Files.FirstOrDefault(f => f.Index == index);
        if (file == null)
            throw new UploadApiException(404, "upload_not_found", "上传会话中不存在该文件。");

        var partPath = PartPath(session.Directory, index);
        var received = GetRawPartLength(partPath);
        if (received != offset)
        {
            throw new UploadApiException(409, "offset_mismatch",
                $"分片偏移不匹配：服务端已收到 {received} 字节，请从该位置继续上传。", received);
        }

        if (offset > file.Length || contentLength > file.Length - offset)
        {
            throw new UploadApiException(400, "chunk_too_large",
                $"本次分片超出文件“{file.Name}”的声明大小（{file.Length} 字节）。", received, file.Length);
        }

        long written = 0;
        try
        {
            await using (var target = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    while (written < contentLength)
                    {
                        var want = (int)Math.Min(buffer.Length, contentLength - written);
                        var read = await body.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                        if (read <= 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                    }

                    await target.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RollbackPart(partPath, received);
            throw;
        }
        catch (Exception ex)
        {
            RollbackPart(partPath, received);
            _log.Warn($"写入上传分片失败（{uploadId}/{index}）：{ex.Message}");
            throw new UploadApiException(400, "invalid_request", "分片写入失败，请重试。", received, file.Length);
        }

        if (written != contentLength)
        {
            RollbackPart(partPath, received);
            throw new UploadApiException(400, "invalid_request", "分片数据不完整，请重试。", received, file.Length);
        }

        received += written;
        return new UploadPutView(index, received, file.Length, received >= file.Length);
    }

    public async Task<ShareRecord> CompleteAsync(string uploadId, string ownerId, CancellationToken ct = default)
    {
        if (!RandomToken.IsValidId(uploadId))
            throw new UploadApiException(404, "upload_not_found", "上传会话不存在或已过期。");

        using var lease = await EnterAsync(uploadId, ct).ConfigureAwait(false);
        var session = await ReadSessionLockedAsync(uploadId, ownerId, ct).ConfigureAwait(false);
        if (session == null)
            throw new UploadApiException(404, "upload_not_found", "上传会话不存在或已过期。");

        var manifest = session.Manifest;
        long receivedBytes = 0;
        long totalBytes = 0;
        var incomplete = false;
        foreach (var file in manifest.Files)
        {
            var raw = GetRawPartLength(PartPath(session.Directory, file.Index));
            if (raw > long.MaxValue - receivedBytes || file.Length > long.MaxValue - totalBytes)
                throw new UploadApiException(400, "invalid_request", "上传会话的文件大小无效。");
            receivedBytes += Math.Min(raw, file.Length);
            totalBytes += file.Length;
            if (raw != file.Length) incomplete = true;
        }

        if (incomplete)
        {
            throw new UploadApiException(409, "upload_incomplete",
                $"分片尚未全部上传完成（已收到 {receivedBytes}/{totalBytes} 字节）。", receivedBytes, totalBytes);
        }

        var stagingDirectory = Path.Combine(session.Directory, ".complete-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var uploads = new List<UploadedFile>(manifest.Files.Count);
            foreach (var file in manifest.Files)
            {
                var source = PartPath(session.Directory, file.Index);
                if (!File.Exists(source))
                    throw new UploadApiException(409, "upload_incomplete", "上传会话缺少分片文件，请重新上传。");

                var staged = Path.Combine(stagingDirectory,
                    file.Index.ToString(CultureInfo.InvariantCulture) + ".part");
                HardLinkOrCopy(source, staged);
                uploads.Add(new UploadedFile(staged, file.Name, file.Length));
            }

            var share = await _shares.CreateFromUploadsAsync(
                uploads,
                manifest.OwnerId,
                TimeSpan.FromMinutes(manifest.ExpiryMinutes),
                manifest.Burn,
                manifest.Title ?? "",
                ct).ConfigureAwait(false);

            if (!TryDeleteDirectory(session.Directory))
            {
                // Do not let a failed cleanup turn a successful share into a client-visible failure.
                try { File.Delete(ManifestPath(session.Directory)); } catch { /* best effort */ }
                _log.Warn($"分享 {share.Id} 已创建，但上传会话目录清理失败：{session.Directory}");
            }

            _log.Info($"分片上传会话 {uploadId} 已完成，创建分享 {share.Id}。");
            return share;
        }
        catch (UploadApiException)
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(stagingDirectory);
            _log.Error($"完成分片上传会话 {uploadId} 失败。", ex);
            throw new UploadApiException(500, "create_failed", "创建分享失败，请稍后重试。");
        }
    }

    public async Task<bool> DeleteAsync(string uploadId, string ownerId, CancellationToken ct = default)
    {
        if (!RandomToken.IsValidId(uploadId)) return false;
        using var lease = await EnterAsync(uploadId, ct).ConfigureAwait(false);
        var session = await ReadSessionLockedAsync(uploadId, ownerId, ct).ConfigureAwait(false);
        if (session == null) return false;
        if (!TryDeleteDirectory(session.Directory))
            throw new UploadApiException(500, "internal_error", "删除上传会话失败，请稍后重试。");
        _log.Info($"已删除分片上传会话 {uploadId}。");
        return true;
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var root = GetUploadsRoot();
        if (!Directory.Exists(root)) return 0;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            _log.Warn($"枚举上传会话目录失败：{ex.Message}");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var cleaned = 0;
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!RandomToken.IsValidId(id))
            {
                if (IsOlderThan(directory, now, OrphanRetention) && TryDeleteDirectory(directory)) cleaned++;
                continue;
            }

            try
            {
                using var lease = await EnterAsync(id, ct).ConfigureAwait(false);
                UploadManifest? manifest = null;
                var manifestPath = ManifestPath(directory);
                try
                {
                    if (File.Exists(manifestPath))
                        manifest = await JsonFile.ReadAsync<UploadManifest>(manifestPath, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"读取上传会话清单失败：{manifestPath}：{ex.Message}");
                }

                var delete = false;
                if (manifest != null)
                {
                    if (manifest.ExpiresAt <= now)
                    {
                        delete = true;
                    }
                    else
                    {
                        CleanupStaleStaging(directory, now);
                    }
                }
                else if (IsOlderThan(directory, now, OrphanRetention))
                {
                    delete = true;
                }

                if (delete && TryDeleteDirectory(directory))
                {
                    cleaned++;
                    _log.Info($"已清理过期上传会话 {id}。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"清理上传会话失败：{directory}：{ex.Message}");
            }
        }

        return cleaned;
    }

    private void CleanupStaleStaging(string sessionDirectory, DateTimeOffset now)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(sessionDirectory, ".complete-*"))
            {
                if (IsOlderThan(directory, now, StagingRetention)) TryDeleteDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理上传暂存目录失败：{sessionDirectory}：{ex.Message}");
        }
    }

    private async Task<Session?> ReadSessionLockedAsync(string uploadId, string ownerId, CancellationToken ct)
    {
        var directory = SessionPath(uploadId);
        if (!Directory.Exists(directory)) return null;

        UploadManifest? manifest;
        try
        {
            manifest = await JsonFile.ReadAsync<UploadManifest>(ManifestPath(directory), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取上传会话清单失败：{uploadId}：{ex.Message}");
            return null;
        }

        if (!IsValidManifest(manifest, uploadId, ownerId)) return null;

        if (manifest!.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            TryDeleteDirectory(directory);
            return null;
        }

        return new Session(directory, manifest);
    }

    private static bool IsValidManifest(UploadManifest? manifest, string uploadId, string ownerId)
    {
        if (manifest == null) return false;
        if (!string.Equals(manifest.UploadId, uploadId, StringComparison.Ordinal)) return false;
        if (string.IsNullOrWhiteSpace(manifest.OwnerId) ||
            !string.Equals(manifest.OwnerId, ownerId, StringComparison.Ordinal)) return false;
        if (manifest.ExpiryMinutes is < 1 or > 43200) return false;
        if (manifest.Files == null || manifest.Files.Count is < 1 or > MaxFiles) return false;

        var indexes = new HashSet<int>();
        foreach (var file in manifest.Files)
        {
            if (file == null || file.Index < 0 || file.Length < 0) return false;
            if (string.IsNullOrWhiteSpace(file.Name)) return false;
            if (!indexes.Add(file.Index)) return false;
        }

        return true;
    }

    private static UploadSessionView BuildView(UploadManifest manifest, string directory)
    {
        var files = manifest.Files.Select(file => new UploadFileView(
            file.Index,
            file.Name,
            file.Length,
            Math.Min(GetRawPartLength(PartPath(directory, file.Index)), file.Length))).ToList();
        return new UploadSessionView(manifest.UploadId, ChunkSize, manifest.ExpiresAt, files);
    }

    private string GetUploadsRoot()
    {
        var settings = _settings.Current;
        var storageRoot = SettingsService.ResolveStorageDirectory(settings, _settings.DataDirectory);
        return Path.Combine(storageRoot, ".tmp", "uploads");
    }

    private string SessionPath(string uploadId) => Path.Combine(GetUploadsRoot(), uploadId);

    private static string ManifestPath(string directory) => Path.Combine(directory, "manifest.json");

    private static string PartPath(string directory, int index)
        => Path.Combine(directory, index.ToString(CultureInfo.InvariantCulture) + ".part");

    private static long GetRawPartLength(string partPath)
    {
        try
        {
            var info = new FileInfo(partPath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void RollbackPart(string partPath, long previousLength)
    {
        try
        {
            if (previousLength <= 0)
            {
                if (File.Exists(partPath)) File.Delete(partPath);
                return;
            }

            using var target = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None);
            target.SetLength(previousLength);
        }
        catch (Exception ex)
        {
            _log.Warn($"回滚上传分片失败：{partPath}：{ex.Message}");
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
            _log.Warn($"创建上传暂存硬链接失败，将复制文件：{source}：{ex.Message}");
        }

        File.Copy(source, destination, overwrite: false);
    }

    private bool TryDeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return true;
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                _log.Warn($"删除目录失败：{directory}：{ex.Message}");
                return false;
            }
        }

        return false;
    }

    private static bool IsOlderThan(string path, DateTimeOffset now, TimeSpan age)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path) < (now - age).UtcDateTime;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SessionLease> EnterAsync(string uploadId, CancellationToken ct)
    {
        LockEntry entry;
        lock (_lockGate)
        {
            if (!_locks.TryGetValue(uploadId, out entry!))
            {
                entry = new LockEntry();
                _locks[uploadId] = entry;
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReturnLock(uploadId, entry);
            throw;
        }

        return new SessionLease(this, uploadId, entry);
    }

    private void ReturnLock(string uploadId, LockEntry entry)
    {
        lock (_lockGate)
        {
            if (--entry.ReferenceCount == 0 &&
                _locks.TryGetValue(uploadId, out var current) &&
                ReferenceEquals(current, entry))
            {
                _locks.Remove(uploadId);
            }
        }
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
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int LinkUnix(string oldpath, string newpath);

    private sealed class LockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int ReferenceCount;
    }

    private sealed class SessionLease : IDisposable
    {
        private readonly UploadSessionService _owner;
        private readonly string _uploadId;
        private readonly LockEntry _entry;
        private bool _disposed;

        public SessionLease(UploadSessionService owner, string uploadId, LockEntry entry)
        {
            _owner = owner;
            _uploadId = uploadId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _entry.Semaphore.Release();
            _owner.ReturnLock(_uploadId, _entry);
        }
    }

    private sealed class Session
    {
        public Session(string directory, UploadManifest manifest)
        {
            Directory = directory;
            Manifest = manifest;
        }

        public string Directory { get; }
        public UploadManifest Manifest { get; }
    }

    private sealed class UploadManifest
    {
        public string UploadId { get; set; } = "";
        public string OwnerId { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int ExpiryMinutes { get; set; }
        public bool Burn { get; set; }
        public string? Title { get; set; }
        public List<UploadManifestFile> Files { get; set; } = new();
    }

    private sealed class UploadManifestFile
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public long Length { get; set; }
        public long? LastModified { get; set; }
    }
}
