using System.Globalization;
using System.Text;
using System.Text.Json;
using LanShare.Core;
using LanShare.Core.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LanShare.Server.Hosting;

/// <summary>Terminal HTTP pipeline: JSON API, share page, downloads and embedded static assets.</summary>
internal sealed class RequestPipeline
{
    private readonly ISettingsService _settings;
    private readonly IShareService _shares;
    private readonly FileLogger _log;
    private readonly string _version;
    private readonly Func<double> _uptime;
    private readonly UploadReader _formReader;
    private readonly UploadSessionService _uploads;
    private readonly EmbeddedAssetStore _assets;

    public RequestPipeline(ISettingsService settings, IShareService shares, UploadSessionService uploads,
        FileLogger log, string version, Func<double> uptime)
    {
        _settings = settings;
        _shares = shares;
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _log = log;
        _version = version;
        _uptime = uptime;
        _formReader = new UploadReader(settings, log);
        _assets = new EmbeddedAssetStore(typeof(RequestPipeline).Assembly, log);
    }

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            await RouteAsync(context).ConfigureAwait(false);
        }
        catch (UploadApiException ex)
        {
            if (!context.Response.HasStarted)
            {
                await JsonHttp.WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message,
                    context.RequestAborted, ex.Received, ex.Total).ConfigureAwait(false);
            }
            else
            {
                context.Abort();
            }
        }
        catch (ShareTooLargeException ex)
        {
            if (!context.Response.HasStarted)
                await JsonHttp.WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "too_large", ex.Message);
            else
                context.Abort();
        }
        catch (RequestValidationException ex)
        {
            if (!context.Response.HasStarted)
                await JsonHttp.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", ex.Message);
            else
                context.Abort();
        }
        catch (BadHttpRequestException ex)
        {
            if (!context.Response.HasStarted)
                await JsonHttp.WriteErrorAsync(context, ex.StatusCode,
                    ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "too_large" : "bad_request",
                    ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? "上传内容过大，超过服务端限制。" : "请求格式无效。");
            else
                context.Abort();
        }
        catch (InvalidDataException)
        {
            if (!context.Response.HasStarted)
                await JsonHttp.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request",
                    "请求格式无效：无法解析上传内容。");
            else
                context.Abort();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser went away; nothing useful can be written.
            _log.Info($"客户端中断了请求：{context.Request.Method} {context.Request.Path}");
        }
        catch (Exception ex)
        {
            _log.Error($"处理请求失败：{context.Request.Method} {context.Request.Path}", ex);
            if (!context.Response.HasStarted)
            {
                await JsonHttp.WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "internal_error",
                    "服务器内部错误，请稍后重试。");
            }
            else
            {
                context.Abort();
            }
        }
    }

    private async Task RouteAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";

        if (IsApiPath(path))
        {
            await HandleApiAsync(context, path, method).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/d/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDownloadAsync(context, path[3..], method).ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await MethodNotAllowedAsync(context, "GET, HEAD").ConfigureAwait(false);
            return;
        }

        if (path == "/" || path.Length == 0)
        {
            JsonHttp.EnsureOwnerCookie(context);
            await ServeAssetAsync(context, "index.html").ConfigureAwait(false);
            return;
        }

        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            await ServeAssetAsync(context, "favicon.ico").ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            JsonHttp.EnsureOwnerCookie(context);
            await ServeAssetAsync(context, "share.html").ConfigureAwait(false);
            return;
        }

        var relative = path.TrimStart('/');
        if (!string.IsNullOrEmpty(relative) && _assets.TryGet(relative, out var asset))
        {
            await WriteAssetAsync(context, asset).ConfigureAwait(false);
            return;
        }

        await WriteNotFoundPageAsync(context).ConfigureAwait(false);
    }

    private static bool IsApiPath(string path)
        => path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private async Task HandleApiAsync(HttpContext context, string path, string method)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ct = context.RequestAborted;

        if (segments.Length == 2 && segments[1].Equals("health", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(method)) { await MethodNotAllowedAsync(context, "GET"); return; }
            await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new
            {
                status = "ok",
                version = _version,
                serverTime = DateTimeOffset.UtcNow,
                uptimeSeconds = (long)Math.Max(0, Math.Floor(_uptime()))
            }, ct).ConfigureAwait(false);
            return;
        }

        if (segments.Length == 2 && segments[1].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(method)) { await MethodNotAllowedAsync(context, "GET"); return; }
            var settings = _settings.Current;
            var addresses = NetworkInfo.GetIPv4Addresses()
                .Select(a => new { interfaceName = a.InterfaceName, address = a.Address, description = a.Description })
                .ToList();
            await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new
            {
                maxUploadBytes = settings.MaxUploadBytes,
                defaultExpiryMinutes = settings.DefaultExpiryMinutes,
                burnAfterDownload = settings.BurnAfterDownloadByDefault,
                port = settings.Port,
                internalBaseUrl = LinkBuilder.InternalBase(settings),
                externalBaseUrl = string.IsNullOrWhiteSpace(settings.ExternalBaseUrl)
                    ? ""
                    : LinkBuilder.ExternalBase(settings),
                productName = LanShareConstants.ProductName,
                version = _version,
                lanAddresses = addresses
            }, ct).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 2 && segments[1].Equals("uploads", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 2)
            {
                await HandleUploadsCollectionAsync(context, method, ct).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 3)
            {
                await HandleUploadSessionAsync(context, segments[2], method, ct).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 4 && segments[3].Equals("complete", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUploadCompleteAsync(context, segments[2], method, ct).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 4)
            {
                await HandleUploadChunkAsync(context, segments[2], segments[3], method, ct).ConfigureAwait(false);
                return;
            }
        }

        if (segments.Length >= 2 && segments[1].Equals("shares", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 2)
            {
                await HandleSharesCollectionAsync(context, method, ct).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 3)
            {
                await HandleShareItemAsync(context, segments[2], method, ct).ConfigureAwait(false);
                return;
            }

            if (segments.Length == 4 && segments[3].Equals("revoke", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRevokeAsync(context, segments[2], method, ct).ConfigureAwait(false);
                return;
            }
        }

        if (segments.Length == 4 &&
            segments[1].Equals("public", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("shares", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePublicShareAsync(context, segments[3], method, ct).ConfigureAwait(false);
            return;
        }

        await JsonHttp.WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", "接口不存在。", ct)
            .ConfigureAwait(false);
    }

    private const long MaxJsonBodyBytes = 1024 * 1024;

    private async Task HandleUploadsCollectionAsync(HttpContext context, string method, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(method))
        {
            await MethodNotAllowedAsync(context, "POST").ConfigureAwait(false);
            return;
        }

        var owner = JsonHttp.EnsureOwnerCookie(context);
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求格式无效：请使用 application/json 提交上传会话。");
        }

        var payload = await ReadUploadCreatePayloadAsync(context.Request, ct).ConfigureAwait(false);
        var view = await _uploads.CreateAsync(payload.Files, payload.ExpiryMinutes, payload.Burn,
            payload.Title, owner, ct).ConfigureAwait(false);

        context.Response.Headers.Location = "/api/uploads/" + view.UploadId;
        await JsonHttp.WriteAsync(context, StatusCodes.Status201Created, ToUploadResponse(view), ct)
            .ConfigureAwait(false);
    }

    private async Task HandleUploadSessionAsync(HttpContext context, string uploadId, string method,
        CancellationToken ct)
    {
        var owner = JsonHttp.EnsureOwnerCookie(context);

        if (HttpMethods.IsDelete(method))
        {
            var deleted = await _uploads.DeleteAsync(uploadId, owner, ct).ConfigureAwait(false);
            if (!deleted)
            {
                throw new UploadApiException(StatusCodes.Status404NotFound, "upload_not_found",
                    "上传会话不存在或已过期。");
            }

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!HttpMethods.IsGet(method))
        {
            await MethodNotAllowedAsync(context, "GET, DELETE").ConfigureAwait(false);
            return;
        }

        var view = await _uploads.GetAsync(uploadId, owner, ct).ConfigureAwait(false);
        if (view == null)
        {
            throw new UploadApiException(StatusCodes.Status404NotFound, "upload_not_found",
                "上传会话不存在或已过期。");
        }

        context.Response.Headers.CacheControl = "no-store";
        await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, ToUploadResponse(view), ct)
            .ConfigureAwait(false);
    }

    private async Task HandleUploadChunkAsync(HttpContext context, string uploadId, string rawIndex,
        string method, CancellationToken ct)
    {
        if (!HttpMethods.IsPut(method))
        {
            await MethodNotAllowedAsync(context, "PUT").ConfigureAwait(false);
            return;
        }

        var owner = JsonHttp.EnsureOwnerCookie(context);
        if (!int.TryParse(rawIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request", "文件序号无效。");
        }

        var offsetText = context.Request.Query["offset"].ToString();
        if (!long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset < 0)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "缺少有效的 offset 参数。");
        }

        if (context.Request.ContentLength is not long contentLength || contentLength <= 0)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求必须包含 Content-Length，且分片大小必须大于 0。");
        }

        if (contentLength > UploadSessionService.ChunkSize)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                $"单个分片不能超过 {UploadSessionService.ChunkSize} 字节。");
        }

        if (!string.IsNullOrWhiteSpace(context.Request.ContentType) &&
            (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
             !mediaType.MediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求格式无效：分片必须使用 application/octet-stream。");
        }

        var result = await _uploads.AppendAsync(uploadId, owner, index, offset, contentLength,
            context.Request.Body, ct).ConfigureAwait(false);

        await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new
        {
            index = result.Index,
            received = result.Received,
            length = result.Length,
            complete = result.Complete
        }, ct).ConfigureAwait(false);
    }

    private async Task HandleUploadCompleteAsync(HttpContext context, string uploadId, string method,
        CancellationToken ct)
    {
        if (!HttpMethods.IsPost(method))
        {
            await MethodNotAllowedAsync(context, "POST").ConfigureAwait(false);
            return;
        }

        var owner = JsonHttp.EnsureOwnerCookie(context);
        var share = await _uploads.CompleteAsync(uploadId, owner, ct).ConfigureAwait(false);
        var dto = LinkBuilder.ToDto(share, _settings.Current);
        await JsonHttp.WriteAsync(context, StatusCodes.Status201Created, new { share = dto }, ct)
            .ConfigureAwait(false);
    }

    private static object ToUploadResponse(UploadSessionView view) => new
    {
        uploadId = view.UploadId,
        chunkSize = view.ChunkSize,
        expiresAt = view.ExpiresAt,
        files = view.Files.Select(f => new
        {
            index = f.Index,
            name = f.Name,
            length = f.Length,
            received = f.Received
        }).ToList()
    };

    private static async Task<UploadCreatePayload> ReadUploadCreatePayloadAsync(HttpRequest request,
        CancellationToken ct)
    {
        using var document = await ReadJsonDocumentAsync(request, ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求格式无效：JSON 根节点必须是对象。");
        }

        if (!TryGetProperty(root, "files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求格式无效：缺少 files 数组。");
        }

        var files = new List<UploadCreateFile>();
        foreach (var item in filesElement.EnumerateArray())
        {
            if (files.Count >= UploadSessionService.MaxFiles)
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    $"一次最多上传 {UploadSessionService.MaxFiles} 个文件。");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "请求格式无效：files 中的每一项都必须是对象。");
            }

            if (!TryGetProperty(item, "name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "请求格式无效：每个文件都必须包含非空的 name。");
            }

            if (!TryGetProperty(item, "length", out var lengthElement) ||
                lengthElement.ValueKind != JsonValueKind.Number ||
                !lengthElement.TryGetInt64(out var length) || length < 0)
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "请求格式无效：每个文件都必须包含非负整数的 length。");
            }

            long? lastModified = null;
            if (TryGetProperty(item, "lastModified", out var modifiedElement) &&
                modifiedElement.ValueKind != JsonValueKind.Null)
            {
                if (modifiedElement.ValueKind != JsonValueKind.Number ||
                    !modifiedElement.TryGetInt64(out var modified))
                {
                    throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                        "请求格式无效：lastModified 必须是整数时间戳。");
                }

                lastModified = modified;
            }

            files.Add(new UploadCreateFile(nameElement.GetString()!, length, lastModified));
        }

        if (files.Count == 0)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请至少选择一个文件后再创建分享。");
        }

        int? expiryMinutes = null;
        if (TryGetProperty(root, "expiryMinutes", out var expiryElement) &&
            expiryElement.ValueKind != JsonValueKind.Null)
        {
            if (expiryElement.ValueKind != JsonValueKind.Number || !expiryElement.TryGetInt32(out var minutes))
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "有效期必须是整数分钟数。");
            }

            expiryMinutes = minutes;
        }

        bool? burn = null;
        if (TryGetProperty(root, "burn", out var burnElement) && burnElement.ValueKind != JsonValueKind.Null)
        {
            if (burnElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "参数 burn 必须是布尔值。");
            }

            burn = burnElement.GetBoolean();
        }

        string? title = null;
        if (TryGetProperty(root, "title", out var titleElement) && titleElement.ValueKind != JsonValueKind.Null)
        {
            if (titleElement.ValueKind != JsonValueKind.String)
            {
                throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                    "参数 title 必须是字符串。");
            }

            var text = titleElement.GetString();
            if (!string.IsNullOrWhiteSpace(text)) title = text.Trim();
        }

        return new UploadCreatePayload(files, expiryMinutes, burn, title);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is long declared && declared > MaxJsonBodyBytes)
        {
            throw new UploadApiException(StatusCodes.Status413PayloadTooLarge, "too_large",
                "上传会话描述内容过大。");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)
                   .ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxJsonBodyBytes)
            {
                throw new UploadApiException(StatusCodes.Status413PayloadTooLarge, "too_large",
                    "上传会话描述内容过大。");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request", "请求内容为空。");
        }

        buffer.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 16 }, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new UploadApiException(StatusCodes.Status400BadRequest, "invalid_request",
                "请求格式无效：JSON 内容无法解析。");
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record UploadCreatePayload(List<UploadCreateFile> Files, int? ExpiryMinutes, bool? Burn,
        string? Title);

    private async Task HandleSharesCollectionAsync(HttpContext context, string method, CancellationToken ct)
    {
        var owner = JsonHttp.EnsureOwnerCookie(context);
        if (HttpMethods.IsGet(method))
        {
            var shares = await _shares.GetByOwnerAsync(owner, ct).ConfigureAwait(false);
            var settings = _settings.Current;
            var dto = shares.Select(s => LinkBuilder.ToDto(s, settings)).ToList();
            await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new { shares = dto }, ct).ConfigureAwait(false);
            return;
        }

        if (!HttpMethods.IsPost(method))
        {
            await MethodNotAllowedAsync(context, "GET, POST").ConfigureAwait(false);
            return;
        }

        var form = await _formReader.ReadAsync(context, ct).ConfigureAwait(false);
        var settingsNow = _settings.Current;
        var expiryMinutes = form.ExpiryMinutes ?? settingsNow.DefaultExpiryMinutes;
        if (expiryMinutes is < 1 or > 43200)
        {
            throw new RequestValidationException("有效期必须是 1 到 43200 分钟之间的整数。");
        }

        var burn = form.Burn ?? settingsNow.BurnAfterDownloadByDefault;
        var record = await _shares.CreateFromUploadsAsync(
            form.Files,
            owner,
            TimeSpan.FromMinutes(expiryMinutes),
            burn,
            form.Title ?? "",
            ct).ConfigureAwait(false);

        await JsonHttp.WriteAsync(context, StatusCodes.Status201Created,
            new { share = LinkBuilder.ToDto(record, _settings.Current) }, ct).ConfigureAwait(false);
    }

    private async Task HandleShareItemAsync(HttpContext context, string id, string method, CancellationToken ct)
    {
        if (!HttpMethods.IsGet(method))
        {
            await MethodNotAllowedAsync(context, "GET").ConfigureAwait(false);
            return;
        }

        var owner = JsonHttp.EnsureOwnerCookie(context);
        var share = await _shares.GetAsync(id, ct).ConfigureAwait(false);
        if (share == null || !string.Equals(share.OwnerId, owner, StringComparison.Ordinal))
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found",
                "分享不存在或无权访问。", ct).ConfigureAwait(false);
            return;
        }

        if (share.IsRevoked)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone, "revoked",
                "该分享已被撤销。", ct).ConfigureAwait(false);
            return;
        }

        if (share.IsExpired)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone, "expired",
                "该分享已过期。", ct).ConfigureAwait(false);
            return;
        }

        await JsonHttp.WriteAsync(context, StatusCodes.Status200OK,
            new { share = LinkBuilder.ToDto(share, _settings.Current) }, ct).ConfigureAwait(false);
    }

    private async Task HandleRevokeAsync(HttpContext context, string id, string method, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(method))
        {
            await MethodNotAllowedAsync(context, "POST").ConfigureAwait(false);
            return;
        }

        var share = await _shares.GetAsync(id, ct).ConfigureAwait(false);
        if (share == null)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found",
                "分享不存在。", ct).ConfigureAwait(false);
            return;
        }

        var owner = JsonHttp.ReadOwnerCookie(context);
        if (owner == null || !string.Equals(share.OwnerId, owner, StringComparison.Ordinal))
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden",
                "只有分享的创建者才能撤销该分享。", ct).ConfigureAwait(false);
            return;
        }

        if (share.IsRevoked || share.IsExpired)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone,
                share.IsRevoked ? "revoked" : "expired",
                share.IsRevoked ? "该分享已被撤销。" : "该分享已过期。", ct).ConfigureAwait(false);
            return;
        }

        var revoked = await _shares.RevokeAsync(id, owner, true, ct).ConfigureAwait(false);
        if (!revoked)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone, "gone",
                "该分享已失效。", ct).ConfigureAwait(false);
            return;
        }

        await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new { ok = true }, ct).ConfigureAwait(false);
    }

    private async Task HandlePublicShareAsync(HttpContext context, string id, string method, CancellationToken ct)
    {
        if (!HttpMethods.IsGet(method))
        {
            await MethodNotAllowedAsync(context, "GET").ConfigureAwait(false);
            return;
        }

        var share = await _shares.GetAsync(id, ct).ConfigureAwait(false);
        if (share == null)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found",
                "分享不存在或链接有误。", ct).ConfigureAwait(false);
            return;
        }

        if (share.IsRevoked)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone, "revoked",
                "该分享已被分享者撤销，文件已删除。", ct).ConfigureAwait(false);
            return;
        }

        if (share.IsExpired)
        {
            await JsonHttp.WriteErrorAsync(context, StatusCodes.Status410Gone, "expired",
                "该分享已过期，文件已被自动清理。", ct).ConfigureAwait(false);
            return;
        }

        var payload = new
        {
            id = share.Id,
            displayName = share.DisplayName,
            downloadName = share.DownloadName,
            totalBytes = share.TotalBytes,
            createdAt = share.CreatedAt,
            expiresAt = share.ExpiresAt,
            burnAfterDownload = share.BurnAfterDownload,
            downloadCount = share.DownloadCount,
            isExpired = share.IsExpired,
            isRevoked = share.IsRevoked,
            isPackaged = share.IsPackaged,
            downloadUrl = "/d/" + share.Id,
            files = share.Files.Select(f => new { name = f.Name, length = f.Length }).ToList()
        };
        await JsonHttp.WriteAsync(context, StatusCodes.Status200OK, new { share = payload }, ct).ConfigureAwait(false);
    }

    private async Task HandleDownloadAsync(HttpContext context, string rawId, string method)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await MethodNotAllowedAsync(context, "GET, HEAD").ConfigureAwait(false);
            return;
        }

        var id = rawId.Trim('/');
        var ct = context.RequestAborted;
        var share = await _shares.GetAsync(id, ct).ConfigureAwait(false);
        if (share == null)
        {
            await WriteDownloadErrorAsync(context, StatusCodes.Status404NotFound, "链接无效", "没有找到对应的分享。");
            return;
        }

        if (share.IsRevoked)
        {
            await WriteDownloadErrorAsync(context, StatusCodes.Status410Gone, "分享已被撤销", "分享者已撤销该分享，文件已删除。");
            return;
        }

        if (share.IsExpired)
        {
            await WriteDownloadErrorAsync(context, StatusCodes.Status410Gone, "分享已过期", "该分享已过期，文件已被自动清理。");
            return;
        }

        var path = _shares.GetDownloadPath(share);
        if (path == null)
        {
            await WriteDownloadErrorAsync(context, StatusCodes.Status404NotFound, "文件不存在", "分享的文件已被清理。");
            return;
        }

        FileInfo info;
        long length;
        DateTime lastWriteUtc;
        try
        {
            info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists)
            {
                await WriteDownloadErrorAsync(context, StatusCodes.Status404NotFound, "文件不存在", "分享的文件已被清理。");
                return;
            }

            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            _log.Error($"读取下载文件信息失败：{path}", ex);
            await WriteDownloadErrorAsync(context, StatusCodes.Status500InternalServerError, "读取失败", "无法读取分享文件。");
            return;
        }

        var shortId = id.Length > 8 ? id[..8] : id;
        var etag = $"\"{shortId}-{length}-{lastWriteUtc.Ticks}\"";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["ETag"] = etag;
        context.Response.Headers["Last-Modified"] = lastWriteUtc.ToString("R", CultureInfo.InvariantCulture);

        if (IfNoneMatchMatches(context.Request.Headers["If-None-Match"].ToString(), etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var start = 0L;
        var end = length - 1;
        var partial = false;
        var rangeText = context.Request.Headers["Range"].ToString();
        if (length > 0 && !string.IsNullOrWhiteSpace(rangeText) &&
            RangeHeaderValue.TryParse(rangeText, out var rangeHeader) &&
            rangeHeader.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) &&
            rangeHeader.Ranges.Count == 1)
        {
            var range = rangeHeader.Ranges.First();
            if (range.From.HasValue)
            {
                start = range.From.Value;
                end = range.To ?? length - 1;
            }
            else if (range.To.HasValue)
            {
                start = Math.Max(0, length - range.To.Value);
                end = length - 1;
            }
            else
            {
                start = 0;
                end = length - 1;
            }

            if (start < 0 || start >= length || end < start)
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers["Content-Range"] = $"bytes */{length}";
                context.Response.ContentLength = 0;
                return;
            }

            end = Math.Min(end, length - 1);
            partial = true;
        }

        var downloadName = _shares.GetDownloadName(share);
        var count = length == 0 ? 0 : end - start + 1;
        var fullDelivery = !partial || (start == 0 && count == length);
        var isHead = HttpMethods.IsHead(method);

        if (!isHead && (fullDelivery || start == 0))
        {
            var registered = await _shares.RegisterDownloadAsync(id, ct).ConfigureAwait(false);
            if (registered == null)
            {
                await WriteDownloadErrorAsync(context, StatusCodes.Status410Gone, "文件不可用", "该分享已失效或文件已被删除。");
                return;
            }

            share = registered;
        }

        context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        context.Response.ContentType = path.EndsWith(LanShareConstants.DefaultArchiveExtension,
            StringComparison.OrdinalIgnoreCase)
            ? "application/x-7z-compressed"
            : "application/octet-stream";
        context.Response.Headers["Content-Disposition"] = BuildContentDisposition(downloadName);
        context.Response.ContentLength = count;
        if (partial)
        {
            context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }

        if (isHead) return;
        if (count == 0)
        {
            if (fullDelivery && share.BurnAfterDownload)
            {
                try
                {
                    await _shares.CompleteDownloadAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"完成空文件下载后的清理失败：{id}", ex);
                }
            }
            return;
        }

        try
        {
            await context.Response.SendFileAsync(path, start, count, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"发送文件失败（{path}）：{ex.Message}");
            if (!context.Response.HasStarted) context.Abort();
            return;
        }

        if (!fullDelivery || !share.BurnAfterDownload) return;
        try
        {
            await _shares.CompleteDownloadAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"完成下载后的清理失败：{id}", ex);
        }
    }

    private static bool IfNoneMatchMatches(string header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var raw in header.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            if (token == "*") return true;
            if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) token = token[2..].Trim();
            if (string.Equals(token, etag, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string BuildContentDisposition(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName.Trim();
        var fallback = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch >= 0x20 && ch <= 0x7e && ch != '"' && ch != '\\') fallback.Append(ch);
            else fallback.Append('_');
        }
        if (fallback.Length == 0) fallback.Append("download");

        var encoded = Uri.EscapeDataString(name).Replace("'", "%27");
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
    }

    private async Task ServeAssetAsync(HttpContext context, string relative)
    {
        if (!_assets.TryGet(relative, out var asset))
        {
            if (relative.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("share.html", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = EmbeddedAssetStore.FallbackPage(LanShareConstants.ProductName,
                    "网页界面未能加载（构建时缺少 wwwroot 资源）。后端服务仍在运行，可访问 /api/health 检查状态。");
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength = bytes.Length;
                if (!HttpMethods.IsHead(context.Request.Method))
                    await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await WriteNotFoundPageAsync(context).ConfigureAwait(false);
            return;
        }

        await WriteAssetAsync(context, asset).ConfigureAwait(false);
    }

    private static async Task WriteAssetAsync(HttpContext context, EmbeddedAsset asset)
    {
        var ifNoneMatch = context.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) &&
            ifNoneMatch.Split(',', StringSplitOptions.TrimEntries).Contains(asset.ETag, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers["ETag"] = asset.ETag;
            context.Response.Headers["Cache-Control"] = CacheControlFor(asset.Path);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = asset.ContentType;
        context.Response.Headers["ETag"] = asset.ETag;
        context.Response.Headers["Cache-Control"] = CacheControlFor(asset.Path);
        context.Response.ContentLength = asset.Content.Length;

        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.Body.WriteAsync(asset.Content, context.RequestAborted).ConfigureAwait(false);
    }

    private static string CacheControlFor(string path)
        => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? "no-cache"
            : "public, max-age=3600";

    private static async Task WriteNotFoundPageAsync(HttpContext context)
    {
        var bytes = EmbeddedAssetStore.FallbackPage("页面不存在", "你访问的页面不存在，请检查链接是否正确。");
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteDownloadErrorAsync(HttpContext context, int status, string title, string message)
    {
        var bytes = EmbeddedAssetStore.FallbackPage(title, message);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task MethodNotAllowedAsync(HttpContext context, string allow)
    {
        context.Response.Headers["Allow"] = allow;
        await JsonHttp.WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed",
            "请求方法不被允许。").ConfigureAwait(false);
    }
}
