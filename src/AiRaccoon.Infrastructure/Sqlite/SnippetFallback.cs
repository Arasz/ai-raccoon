using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     ~200-char snippet for hits without an FTS5 snippet (FR-NM-4 s1; see docs/work/features-native-memory/native-memory.feature).
///     ADR-0035: when <paramref name="query" /> literally occurs in the value, the window centers
///     on that match; otherwise it falls back to a window whose start derives from the entry hash,
///     so long values do not always open on their head.
/// </summary>
internal static partial class SnippetFallback
{
    public const int WindowChars = 200;

    public static string From(string value, string hash, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(hash);

        if (value.Length <= WindowChars)
        {
            return value;
        }

        var maxStart = value.Length - WindowChars;
        var start = QueryMatchStart(value, query, maxStart) ?? HashSeededStart(value, hash, maxStart);

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

    /// <summary>The earliest literal occurrence of a query token in value, centered in the window; null when no token matches.</summary>
    private static int? QueryMatchStart(string value, string? query, int maxStart)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var earliest = -1;
        foreach (Match token in TokenPattern().Matches(query))
        {
            var index = value.IndexOf(token.Value, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (earliest < 0 || index < earliest))
            {
                earliest = index;
            }
        }

        return earliest < 0 ? null : Math.Clamp(earliest - WindowChars / 2, 0, maxStart);
    }

    private static int HashSeededStart(string value, string hash, int maxStart) => (int)(BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(hash)), 0) % (uint)(maxStart + 1));

    [GeneratedRegex(@"\w{2,}")]
    private static partial Regex TokenPattern();
}
