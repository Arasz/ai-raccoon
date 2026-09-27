namespace AiRaccoon.Core.Chunking;

/// <summary>Trimming text to a token budget. Pure; the chunker and the query path both need it.</summary>
public static class TokenBudget
{
    /// <summary>
    ///     The longest prefix of <paramref name="text" /> that fits <paramref name="maxTokens" />.
    ///     Binary search on characters, because a token count is only obtainable by tokenising.
    /// </summary>
    public static string Trim(string text, int maxTokens, TokenCount countTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(countTokens);

        if (maxTokens <= 0 || countTokens(text) <= maxTokens)
        {
            return text;
        }

        return text[..LongestPrefix(text, maxTokens, countTokens)];
    }

    /// <summary>
    ///     How many leading characters of <paramref name="text" /> to split off as one piece: the longest
    ///     prefix within budget that ends on whitespace, so no word is cut. A word longer than the whole
    ///     budget is hard-cut at the budget instead. Always at least 1, so a split loop terminates.
    /// </summary>
    public static int SplitLength(string text, int maxTokens, TokenCount countTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(countTokens);

        var fits = LongestPrefix(text, maxTokens, countTokens);
        if (fits >= text.Length)
        {
            return text.Length;
        }

        var firstContent = 0;
        while (firstContent < text.Length && char.IsWhiteSpace(text[firstContent]))
        {
            firstContent++;
        }

        for (var length = fits; length > firstContent + 1; length--)
        {
            if (char.IsWhiteSpace(text[length - 1]))
            {
                return length;
            }
        }

        return Math.Max(1, fits);
    }

    /// <summary>Binary search (token count is non-decreasing in prefix length for every tokenizer this
    /// project uses) for the longest prefix within the budget.</summary>
    private static int LongestPrefix(string text, int maxTokens, TokenCount countTokens)
    {
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (countTokens(text[..mid]) <= maxTokens)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
