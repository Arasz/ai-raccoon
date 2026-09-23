using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Source-path-shaped queries (file[#section]) match the file name as an ordered phrase in
///     source_file and the section in the section column (see docs/plans/retrieval-improvement-c.md
///     §3 2c); <see cref="NamesFile" /> then keeps only rows of the file the query names.
/// </summary>
internal static partial class SourcePathQuery
{
    extension(FtsQueryPlan queryPlan)
    {
        public FtsQueryPlan AsPathQuery(string query)
        {
            if (!TryParse(query, out var file, out var expression))
            {
                return queryPlan;
            }

            return queryPlan with { Expression = expression, Fallback = null, IsPathQuery = true, MatchesAllTerms = true, AnchorFile = file };
        }
    }

    public static bool TryBuild(string query, out string ftsExpression) => TryParse(query, out _, out ftsExpression);

    /// <summary>
    ///     True when <paramref name="sourceFile" /> is the file <paramref name="anchorFile" /> names: the same
    ///     path, or a path ending in it at a '/' boundary. A leading '/' makes the anchor absolute. Case-insensitive.
    /// </summary>
    public static bool NamesFile(string anchorFile, string? sourceFile)
    {
        if (sourceFile is null)
        {
            return false;
        }

        return anchorFile.StartsWith('/')
            ? sourceFile.Equals(anchorFile, StringComparison.OrdinalIgnoreCase)
            : sourceFile.Equals(anchorFile, StringComparison.OrdinalIgnoreCase)
              || sourceFile.EndsWith("/" + anchorFile, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string query, out string file, out string ftsExpression)
    {
        file = "";
        ftsExpression = "";
        var match = PathRegex().Match(query.Trim());
        if (!match.Success)
        {
            return false;
        }

        var tokens = TokenRegex().Matches(match.Groups["file"].Value).Select(token => token.Value.ToLowerInvariant()).ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        file = match.Groups["file"].Value;
        ftsExpression = $"{{source_file}} : \"{string.Join(' ', tokens)}\"";
        if (match.Groups["section"].Success)
        {
            ftsExpression += $" AND {{source_file section}} : \"{match.Groups["section"].Value.ToLowerInvariant()}\"";
        }

        return true;
    }

    [GeneratedRegex(@"^(?<file>[\w./-]+\.(?:md|markdown|txt))(?:#(?<section>[\w-]+(?: [\w-]+)*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"[\w]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
