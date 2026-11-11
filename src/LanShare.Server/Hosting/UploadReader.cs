using System.Buffers;
using System.Globalization;
using System.Text;
using LanShare.Core;
using LanShare.Core.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LanShare.Server.Hosting;

internal sealed class RequestValidationException : Exception
{
    public RequestValidationException(string message) : base(message)
    {
    }
}

internal sealed class UploadForm
{
    public List<UploadedFile> Files { get; } = new();
    public int? ExpiryMinutes { get; set; }
    public bool? Burn { get; set; }
    public string? Title { get; set; }
}

/// <summary>Streams a multipart/form-data upload into per-file temp files, enforcing the configured size limit.</summary>
internal sealed class UploadReader
{
    private const int MaxFieldBytes = 8 * 1024;

    private readonly ISettingsService _settings;
    private readonly FileLogger _log;

    public UploadReader(ISettingsService settings, FileLogger log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<UploadForm> ReadAsync(HttpContext context, CancellationToken ct)
    {
        var form = new UploadForm();
        var created = new List<string>();

        try
        {
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
                !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                throw new RequestValidationException("请求格式无效：请使用 multipart/form-data 上传文件。");
            }

            var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 256)
            {
                throw new RequestValidationException("请求格式无效：缺少 multipart 分隔符。");
            }

            var settings = _settings.Current;
            var maxBytes = Math.Max(0, settings.MaxUploadBytes);
            var storageRoot = SettingsService.ResolveStorageDirectory(settings, _settings.DataDirectory);
            var tempDirectory = Path.Combine(storageRoot, ".tmp");
            Directory.CreateDirectory(tempDirectory);

            if (maxBytes > 0 && context.Request.ContentLength is long declared &&
                declared > maxBytes + 10L * 1024 * 1024)
            {
                throw new ShareTooLargeException(maxBytes, declared);
            }

            var reader = new MultipartReader(boundary, context.Request.Body)
            {
                HeadersLengthLimit = 64 * 1024
            };
            if (maxBytes > 0) reader.BodyLengthLimit = maxBytes + 10L * 1024 * 1024;

            long total = 0;
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (string.Equals(name, "files", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
                {
                    var rawName = HeaderUtilities.RemoveQuotes(
                        disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                    var fileName = NormalizeUploadedName(rawName);
                    if (fileName.Length == 0) continue;

                    var tempPath = Path.Combine(tempDirectory, RandomToken.UrlSafe(16) + ".upload");
                    created.Add(tempPath);
                    long length = 0;

                    await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(81920);
                        try
                        {
                            int read;
                            while ((read = await section.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                                       .ConfigureAwait(false)) > 0)
                            {
                                total += read;
                                if (maxBytes > 0 && total > maxBytes)
                                {
                                    throw new ShareTooLargeException(maxBytes, total);
                                }

                                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                length += read;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }

                    form.Files.Add(new UploadedFile(tempPath, fileName, length));
                }
                else if (string.Equals(name, "expiryMinutes", StringComparison.OrdinalIgnoreCase))
                {
                    var text = await ReadSmallFieldAsync(section.Body, ct).ConfigureAwait(false);
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
                        minutes is < 1 or > 43200)
                    {
                        throw new RequestValidationException("有效期必须是 1 到 43200 分钟之间的整数。");
                    }
                    form.ExpiryMinutes = minutes;
                }
                else if (string.Equals(name, "burn", StringComparison.OrdinalIgnoreCase))
                {
                    var text = await ReadSmallFieldAsync(section.Body, ct).ConfigureAwait(false);
                    form.Burn = ParseBoolean(text, "burn");
                }
                else if (string.Equals(name, "title", StringComparison.OrdinalIgnoreCase))
                {
                    var text = await ReadSmallFieldAsync(section.Body, ct).ConfigureAwait(false);
                    if (text.Length > 120) text = text[..120];
                    form.Title = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                }
                else
                {
                    // Drain unknown fields so the multipart reader can reach the next part.
                    await DrainAsync(section.Body, ct).ConfigureAwait(false);
                }
            }

            if (form.Files.Count == 0)
            {
                throw new RequestValidationException("请至少选择一个文件后再创建分享。");
            }

            return form;
        }
        catch
        {
            foreach (var path in created)
            {
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }

    private static async Task<string> ReadSmallFieldAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFieldBytes);
        try
        {
            var total = 0;
            int read;
            while (total < MaxFieldBytes &&
                   (read = await stream.ReadAsync(buffer.AsMemory(total, MaxFieldBytes - total), ct)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            // Detect (and reject) a field that exceeds the small-field budget.
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) > 0)
            {
                throw new RequestValidationException("表单字段内容过长。");
            }

            return Encoding.UTF8.GetString(buffer, 0, total).Trim();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ParseBoolean(string text, string fieldName)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new RequestValidationException($"参数 {fieldName} 的值无效。");
    }

    internal static string NormalizeUploadedName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim().Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        name = name.Trim().Trim('\0');
        return name.Length > 200 ? name[..200] : name;
    }
}
