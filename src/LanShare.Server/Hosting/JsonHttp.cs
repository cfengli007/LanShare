using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanShare.Core;
using Microsoft.AspNetCore.Http;

namespace LanShare.Server.Hosting;

/// <summary>Shared JSON, cookie and error helpers for the minimal HTTP pipeline.</summary>
internal static class JsonHttp
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    public static Task WriteAsync(HttpContext context, int statusCode, object? value, CancellationToken ct = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method) || value == null) return Task.CompletedTask;
        return JsonSerializer.SerializeAsync(context.Response.Body, value, Options, ct);
    }

    public static Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message,
        CancellationToken ct = default, long? received = null, long? total = null)
    {
        if (received.HasValue || total.HasValue)
        {
            var payload = new Dictionary<string, object?>
            {
                ["error"] = message,
                ["code"] = code
            };
            if (received.HasValue) payload["received"] = received.Value;
            if (total.HasValue) payload["total"] = total.Value;
            return WriteAsync(context, statusCode, payload, ct);
        }

        return WriteAsync(context, statusCode, new { error = message, code }, ct);
    }

    /// <summary>Returns the owner cookie value, generating and setting one when missing or malformed.</summary>
    public static string EnsureOwnerCookie(HttpContext context)
    {
        var existing = ReadOwnerCookie(context);
        if (existing != null) return existing;

        var owner = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(LanShareConstants.OwnerCookieName, owner, new CookieOptions
        {
            Path = LanShareConstants.OwnerCookiePath,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true,
            Secure = false
        });
        return owner;
    }

    public static string? ReadOwnerCookie(HttpContext context)
    {
        var value = context.Request.Cookies[LanShareConstants.OwnerCookieName];
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guid.TryParse(value, out _) ? value.Trim() : null;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;
            if (reader.TokenType == JsonTokenType.String &&
                DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                return value;
            }

            throw new JsonException("无效的时间格式。");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
    }
}
