using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LanShare.Core.Infrastructure;

namespace LanShare.Server.Hosting;

internal sealed class EmbeddedAsset
{
    public EmbeddedAsset(string path, byte[] content, string contentType)
    {
        Path = path;
        Content = content;
        ContentType = contentType;
        ETag = "\"" + Convert.ToHexString(SHA256.HashData(content), 0, 8).ToLowerInvariant() + "-" + content.Length.ToString("x") + "\"";
    }

    public string Path { get; }
    public byte[] Content { get; }
    public string ContentType { get; }
    public string ETag { get; }
}

/// <summary>Loads the web UI that is embedded into the server assembly under wwwroot/.</summary>
internal sealed class EmbeddedAssetStore
{
    private readonly Dictionary<string, EmbeddedAsset> _assets = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddedAssetStore(Assembly assembly, FileLogger? logger = null)
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var relative = ToRelativePath(resource);
            if (relative == null) continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                var asset = new EmbeddedAsset(relative, bytes, MimeTypeFor(relative));
                _assets[relative] = asset;
            }
            catch (Exception ex)
            {
                logger?.Warn($"读取嵌入资源“{resource}”失败：{ex.Message}");
            }
        }

        logger?.Info($"已加载 {_assets.Count} 个嵌入静态资源。");
    }

    public int Count => _assets.Count;

    public IEnumerable<string> Paths => _assets.Keys;

    public bool TryGet(string? requestPath, out EmbeddedAsset asset)
    {
        asset = null!;
        if (!TryNormalize(requestPath, out var normalized)) return false;
        return _assets.TryGetValue(normalized, out asset!);
    }

    private static string? ToRelativePath(string resourceName)
    {
        const string marker = "wwwroot/";
        var index = resourceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return resourceName[(index + marker.Length)..].Replace('\\', '/');

        // Older build layouts flatten directories to dots: LanShare.Server.wwwroot.js.app.js
        const string dotted = ".wwwroot.";
        index = resourceName.IndexOf(dotted, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var tail = resourceName[(index + dotted.Length)..];
            return tail.Replace('.', '/');
        }

        return null;
    }

    public static bool TryNormalize(string? rawPath, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            normalized = "index.html";
            return true;
        }

        var path = rawPath.Replace('\\', '/');
        if (path.IndexOf('\0') >= 0) return false;

        try { path = Uri.UnescapeDataString(path); }
        catch { return false; }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..") return false; // never allow escaping the embedded root
            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            normalized = "index.html";
            return true;
        }

        normalized = string.Join('/', stack);
        return true;
    }

    public static string MimeTypeFor(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".txt" or ".md" => "text/plain; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream"
        };
    }

    /// <summary>Small built-in page used only when the embedded UI is missing (should never happen in a normal build).</summary>
    public static byte[] FallbackPage(string title, string message)
    {
        var html = "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
                   "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                   "<title>" + HtmlEncode(title) + "</title>" +
                   "<style>body{font-family:system-ui,-apple-system,'Segoe UI',sans-serif;margin:0;background:#f5f7fb;color:#1f2937}" +
                   "main{max-width:640px;margin:12vh auto;padding:32px;background:#fff;border-radius:16px;box-shadow:0 10px 40px rgba(15,23,42,.08)}" +
                   "h1{margin:0 0 12px;font-size:24px}p{line-height:1.7;color:#475569}a{color:#2563eb}</style></head>" +
                   "<body><main><h1>" + HtmlEncode(title) + "</h1><p>" + HtmlEncode(message) + "</p>" +
                   "<p>API 状态：<a href=\"/api/health\">/api/health</a></p></main></body></html>";
        return Encoding.UTF8.GetBytes(html);
    }

    public static string HtmlEncode(string value) => System.Net.WebUtility.HtmlEncode(value ?? "");
}
