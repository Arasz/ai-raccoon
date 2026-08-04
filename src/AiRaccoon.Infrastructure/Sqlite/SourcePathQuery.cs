using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Wave 2 (plan C §3 2c): a query shaped like a source path (file[#section], e.g.
///     docs/adr/0011-frontend-chassis-stack.md#decision) is matched against the
///     source_file/section FTS columns with AND semantics — the exact chunk (or the owning
///     file's chunks) is the only match, so it ranks first without body-text noise.
/// </summary>
internal static partial class SourcePathQuery
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "near"
    };

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
