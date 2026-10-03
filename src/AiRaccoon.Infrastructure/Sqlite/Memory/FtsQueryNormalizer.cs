using System.Buffers;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Free-text query -> safe FTS5 MATCH plan (see docs/plans/retrieval-improvement-c.md §3 Wave 1): AND-join with OR fallback
///     for short queries, plain OR for long ones (capped at <see cref="MaxOrTerms" />); terms come only from the token regex.
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

    /// <summary>A query term longer than this matches by its first this-many characters: a term longer than a
    /// whole chunk is stored hard-cut, and only its first piece is guaranteed to hold this much of its start.</summary>
    public const int PrefixLength = 64;

    /// <summary>The most terms a long query's OR join carries; above it, stopwords and repeats are dropped and
    /// the first this-many distinct words are kept, so a pasted dump costs about as much as a sentence.</summary>
    public const int MaxOrTerms = 64;

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
                return new FtsQueryPlan(Term(tokens[0]), null, 1) { MatchesAllTerms = true };
        }


        var bigrams = tokens.Count >= 3
            ? Enumerable.Range(0, tokens.Count - 1)
                .Select(i => $"\"{tokens[i]} {tokens[i + 1]}\"")
                .ToList()
            : [];

        if (tokens.Count <= 4)
        {
            return new FtsQueryPlan(
                string.Join(" AND ", tokens.Select(Term)),
                string.Join(" OR ", rawTokens.Select(Term).Concat(bigrams)),
                tokens.Count)
            { MatchesAllTerms = true };
        }

        var terms = rawTokens.Count <= MaxOrTerms
            ? rawTokens.Select(Term)
            : tokens.Select(Term).Distinct().Take(MaxOrTerms);

        return new FtsQueryPlan(string.Join(" OR ", terms), null, tokens.Count);
    }

    private static string Term(string token) => token.Length > PrefixLength ? token[..PrefixLength] + "*" : token;

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}

/// <summary>Primary FTS5 MATCH expression, the OR-join fallback (null when none), and the content-token count for the under-match check.</summary>
public sealed record FtsQueryPlan(string Expression, string? Fallback, int TokenCount)
{
    public bool IsPathQuery { get; init; }

    /// <summary>The file a file#section anchor names, as typed; null for any other query.</summary>
    public string? AnchorFile { get; init; }

    /// <summary>True when <see cref="Expression" /> requires every content term, so a row it matches contains the whole query.</summary>
    public bool MatchesAllTerms { get; init; }
}
