using System.Security.Cryptography;
using System.Text;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Deterministic ~200-char snippet for hits without an FTS5 snippet (FR-NM-4 s1); the
///     window start derives from the entry hash so long values do not always open on their head.
/// </summary>
internal static class SnippetFallback
{
    public const int WindowChars = 200;

    public static string From(string value, string hash)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(hash);

        if (value.Length <= WindowChars)
        {
            return value;
        }

        var maxStart = value.Length - WindowChars;
        var start = (int)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(hash)), 0)
            % (uint)(maxStart + 1));

        // Slide to word boundaries so the window opens and closes on whole words when possible.
        var nextSpace = value.IndexOf(' ', start);
        if (nextSpace >= 0 && nextSpace < start + WindowChars)
        {
            start = nextSpace + 1;
        }

        var end = Math.Min(value.Length, start + WindowChars);
        var lastSpace = value.LastIndexOf(' ', end - 1);
        if (lastSpace > start)
        {
            end = lastSpace;
        }

        var prefix = start > 0 ? "… " : "";
        var suffix = end < value.Length ? " …" : "";
        return prefix + value[start..end].Trim() + suffix;
    }
}
