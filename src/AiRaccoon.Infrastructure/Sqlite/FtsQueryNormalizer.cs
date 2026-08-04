using System.Buffers;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Turns a free-text query into a safe FTS5 MATCH plan (plan C Wave 1): ≤4 content
///     tokens join with AND (precision) plus an OR-join fallback of all query tokens with
///     quoted bigram phrases when the AND under-matches; longer queries keep the plain OR
///     join of all tokens (the proven pre-Wave-1 expression). Punctuation never reaches
///     the FTS5 grammar — every term comes from the token regex.
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
        ArgumentNullException.ThrowIfNull(query);

        // Content tokens (precision primary) vs the query's full token set (recall OR
        // joins): stopwords are stripped from AND primaries because they drown signal
        // (plan C §2.2), but OR joins keep them — under OR they add BM25 weight without
        // constraining, and removing them measurably regresses baseline rankings (A1).
        var rawTokens = TokenRegex().Matches(query)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => !Reserved.Contains(token))
            .ToList();
        var tokens = rawTokens.Where(token => !Stopwords.Contains(token)).ToList();

        if (tokens.Count == 0)
        {
            return new FtsQueryPlan("", null, 0);
        }

        if (tokens.Count == 1)
        {
            return new FtsQueryPlan(tokens[0], null, 1);
        }

        // Adjacent token pairs as quoted phrases ("shadcn ui"): precision signal for the OR
        // fallback (plan C Wave 1.2). Skipped under AND semantics (no constraint) and in
        // the long-query OR primary, where they measurably regress baseline rankings (A1).
        var bigrams = tokens.Count >= 3
            ? Enumerable.Range(0, tokens.Count - 1)
                .Select(i => $"\"{tokens[i]} {tokens[i + 1]}\"")
                .ToList()
            : [];

        if (tokens.Count <= 4)
        {
            // AND for precision; the OR fallback (with bigrams and the query's stopwords)
            // prevents the zero-match regression measured for unguarded AND (plan C Wave
            // 1.3). The store falls back when the AND primary matches fewer rows than it
            // has terms or fewer than the caller asked for — an AND that small is
            // over-constrained (A6/C2 measured cases).
            return new FtsQueryPlan(
                string.Join(" AND ", tokens),
                string.Join(" OR ", rawTokens.Concat(bigrams)),
                tokens.Count);
        }

        // Long queries keep the proven OR recall of all query tokens: stopword stripping
        // and bigrams each regress baseline rankings here (A1 measured case), so the
        // pre-Wave-1 expression is preserved verbatim.
        return new FtsQueryPlan(string.Join(" OR ", rawTokens), null, tokens.Count);
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}

/// <summary>
///     FTS5 MATCH expression to run plus the OR-join fallback to retry when the primary
///     expression under-matches (null when there is nothing to fall back to), and the
///     content-token count used with the caller's limit to detect an under-matched AND.
/// </summary>
internal sealed record FtsQueryPlan(string Expression, string? Fallback, int TokenCount);
