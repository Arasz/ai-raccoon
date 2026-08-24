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

        return text[..lo];
    }
}
