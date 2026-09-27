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
    ///     in-budget prefix ending just after whitespace, else ending on a keyword-term boundary, else a hard
    ///     cut at the budget (a term longer than the whole budget). Always at least 1, so a split loop terminates.
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

        var afterWhitespace = LastCut(fits, length => length - 1 > firstContent && char.IsWhiteSpace(text[length - 1]));
        if (afterWhitespace > 0 && countTokens(text[..afterWhitespace]) <= maxTokens)
        {
            return afterWhitespace;
        }

        var termBoundary = LastCut(fits, length => length > firstContent && (!IsTermChar(text[length - 1]) || !IsTermChar(text[length])));
        if (termBoundary > 0 && countTokens(text[..termBoundary]) <= maxTokens)
        {
            return termBoundary;
        }

        return Math.Max(1, fits);
    }

    /// <summary>A character keyword search treats as part of a term: the same set its query tokenizer keeps.</summary>
    public static bool IsTermChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int LastCut(int fits, Func<int, bool> isCut)
    {
        for (var length = fits; length > 0; length--)
        {
            if (isCut(length))
            {
                return length;
            }
        }

        return 0;
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
