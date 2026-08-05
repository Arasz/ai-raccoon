using System.Security.Cryptography;
using System.Text;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     Content identity (FR-NM-7; see docs/work/features-native-memory/native-memory.feature): SHA-256 over UTF8(path) concatenated with UTF8(value), no
///     separator — so identical content under different paths yields different hashes. Lowercase hex.
/// </summary>
public static class ContentHash
{
    public static string Of(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);

        var pathBytes = Encoding.UTF8.GetBytes(path);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var bytes = new byte[pathBytes.Length + valueBytes.Length];
        pathBytes.CopyTo(bytes, 0);
        valueBytes.CopyTo(bytes, pathBytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
