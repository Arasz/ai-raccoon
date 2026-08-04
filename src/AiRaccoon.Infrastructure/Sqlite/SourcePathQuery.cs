using System.Buffers;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Source-path-shaped queries (file[#section]) match the source_file/section FTS columns
///     with AND semantics (plan C Wave 2 2c), ranking the exact chunk first.
/// </summary>
internal static partial class SourcePathQuery
{
    // Ordinal is safe (and measurably faster than OrdinalIgnoreCase — see
    // SearchValuesVsHashSetBenchmark) because tokens are lowercased before the check
    // below; the set itself is all-lowercase ASCII.
    private static readonly SearchValues<string> Reserved =
        SearchValues.Create(["and", "or", "not", "near"], StringComparison.Ordinal);

    public static bool TryBuild(string query, out string ftsExpression)
    {
        var match = PathRegex().Match(query.Trim());
        if (!match.Success)
        {
            ftsExpression = "";
            return false;
        }

        var tokens = TokenRegex().Matches(match.Groups["file"].Value)
            .Select(t => t.Value.ToLowerInvariant())
            .ToList();
        if (match.Groups["section"].Success)
        {
            tokens.Add(match.Groups["section"].Value.ToLowerInvariant());
        }

        if (tokens.Count == 0)
        {
            ftsExpression = "";
            return false;
        }

        var columns = match.Groups["section"].Success ? "{source_file section}" : "{source_file}";
        var terms = string.Join(" AND ", tokens.Select(t => Reserved.Contains(t) ? $"\"{t}\"" : t));
        ftsExpression = $"{columns} : ({terms})";
        return true;
    }

    [GeneratedRegex(@"^(?<file>[\w./-]+\.(?:md|markdown|txt))(?:#(?<section>[\w-]+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"[\w]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
