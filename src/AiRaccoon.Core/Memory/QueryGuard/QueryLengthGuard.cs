using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>
///     The caller-visible half of ADR-0071: warns when a query is long enough that the active
///     memory embedding engine's window will likely trim it. Always on, unlike
///     <see cref="QueryGuardService" /> — this is a fact about the embedding window, not a
///     togglable policy, so it runs independently of <see cref="QueryGuardConfigKeys.EnabledGlobal" />
///     and its shadow mode.
///     <para>
///         Chars, not tokens: Core cannot reference the ONNX tokenizer or the embedding manifest
///         (clean-layering), so this is a proxy for the engine's real content-token window, not an
///         exact count. The caller supplies that window's token budget (its own
///         <c>IEmbeddingService.ResolveChunkBudgetFor</c> result — 254 for the bundled model, wider
///         for a manifest model); when it does not, <see cref="BundledBudgetTokens" /> applies. The
///         char threshold scales from the budget using the bundled model's own tokens-to-chars ratio
///         (~1,000 characters per 254 tokens is the rule of thumb for English prose; the real figure
///         varies with content — a 12,952-character markdown table measured 249 tokens because its
///         whitespace padding collapses — ADR-0071), so it is an approximation for every budget, not
///         just the bundled one.
///     </para>
/// </summary>
public static class QueryLengthGuard
{
    public const string WarnPolicyName = "QueryOverEmbeddingWindow";

    /// <summary>The bundled all-MiniLM-L6-v2 model's content-token budget — applied when the caller
    /// does not supply the active engine's real one.</summary>
    public const int BundledBudgetTokens = 254;

    /// <summary>The bundled budget's own char threshold (~1,000 characters of English prose).</summary>
    public const int WarnThresholdChars = 1000;

    /// <summary>Bundled chars-per-token rule of thumb, applied to any other budget so its threshold
    /// scales rather than staying pinned to the bundled model's own number.</summary>
    private const double CharsPerTokenEstimate = (double)WarnThresholdChars / BundledBudgetTokens;

    public static QueryGuardVerdict Evaluate(string query, int budgetTokens = BundledBudgetTokens)
    {
        Guard.IsNotNull(query);
        var thresholdChars = ThresholdCharsFor(budgetTokens);
        return query.Length > thresholdChars
            ? QueryGuardVerdict.Warn(WarnPolicyName, GuidanceFor(budgetTokens, thresholdChars))
            : QueryGuardVerdict.Clean;
    }

    /// <summary>Exact for the bundled budget (no floating-point rounding involved); scaled from the
    /// bundled ratio for any other budget.</summary>
    private static int ThresholdCharsFor(int budgetTokens) =>
        budgetTokens == BundledBudgetTokens
            ? WarnThresholdChars
            : (int)Math.Round(budgetTokens * CharsPerTokenEstimate, MidpointRounding.AwayFromZero);

    private static string GuidanceFor(int budgetTokens, int thresholdChars) =>
        $"This query is over {thresholdChars:N0} characters. Semantic search only embeds roughly the "
        + $"first {budgetTokens:N0} tokens of a query (~{thresholdChars:N0} characters of English "
        + "prose, approximate) — search for the identifying line instead of pasting the whole dump, "
        + "e.g. the exception type, error code, or failing test name. Keyword (FTS) matching still "
        + "searches the query in full.";
}
