using System.Security.Cryptography;

namespace LanShare.Core.Infrastructure;

/// <summary>Cryptographically random URL-safe identifiers (16 bytes => 22 chars).</summary>
public static class RandomToken
{
    public const int DefaultByteLength = 16;

    public static string UrlSafe(int byteLength = DefaultByteLength)
    {
        if (byteLength is < 8 or > 64)
            throw new ArgumentOutOfRangeException(nameof(byteLength), "随机标识长度必须在 8 到 64 字节之间。");

        var buffer = new byte[byteLength];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length is < 8 or > 86) return false;
        foreach (var ch in id)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') continue;
            return false;
        }
        return true;
    }
}
