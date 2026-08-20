using System.Buffers;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Free-text query -> safe FTS5 MATCH plan (see docs/plans/retrieval-improvement-c.md §3 Wave 1): AND-join with OR fallback
///     for short queries, plain OR for long ones; terms come only from the token regex.
/// </summary>
internal static partial class FtsQueryNormalizer
{
    // Ordinal is safe (and measurably faster than OrdinalIgnoreCase — see
    // SearchValuesVsHashSetBenchmark) because every token is lowercased before the
    // membership checks below; the sets themselves are all-lowercase ASCII.
    private static readonly SearchValues<string> Reserved =
        SearchValues.Create(["and", "or", "not", "near"], StringComparison.Ordinal);

    private static readonly SearchValues<string> Stopwords =
        SearchValues.Create(
        [
            "what", "is", "the", "how", "does", "about", "are", "do",
            "can", "should", "will", "would", "could", "has", "have", "been",
            "was", "were", "being", "a", "an", "in", "on", "at", "to",
            "for", "of", "by", "with", "from"
        ], StringComparison.Ordinal);

    public static FtsQueryPlan BuildPlan(string query)
    {
        var rawTokens = TokenRegex().Matches(query)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => !Reserved.Contains(token))
            .ToList();

        var tokens = rawTokens.Where(token => !Stopwords.Contains(token)).ToList();

        switch (tokens.Count)
        {
            case 0:
                return new FtsQueryPlan("", null, 0);
            case 1:
                return new FtsQueryPlan(tokens[0], null, 1);
        }


        var bigrams = tokens.Count >= 3
            ? Enumerable.Range(0, tokens.Count - 1)
                .Select(i => $"\"{tokens[i]} {tokens[i + 1]}\"")
                .ToList()
            : [];

        if (tokens.Count <= 4)
        {
            return new FtsQueryPlan(
                string.Join(" AND ", tokens),
                string.Join(" OR ", rawTokens.Concat(bigrams)),
                tokens.Count);
        }

        return new FtsQueryPlan(string.Join(" OR ", rawTokens), null, tokens.Count);
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}

/// <summary>Primary FTS5 MATCH expression, the OR-join fallback (null when none), and the content-token count for the under-match check.</summary>
public sealed record FtsQueryPlan(string Expression, string? Fallback, int TokenCount)
{
    public bool IsPathQuery { get; init; }
}
