using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Turns a free-text query into a safe FTS5 MATCH expression (P6 owns query
///     normalization): alphanumeric tokens joined with spaces (implicit AND), FTS5 reserved
///     words dropped. Punctuation like ':' or quotes can never reach the FTS5 grammar, so a
///     user query cannot make MATCH throw.
/// </summary>
internal static partial class FtsQueryNormalizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "near"
    };

    public static string Normalize(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return string.Join(' ', TokenRegex().Matches(query)
            .Select(match => match.Value)
            .Where(token => !Reserved.Contains(token))
            .Select(token => token.ToLowerInvariant()));
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}