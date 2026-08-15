using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>
///     The caller-visible half of ADR-0071: warns when a query is long enough that the bundled
///     model's embedding window will likely trim it. Always on, unlike <see cref="QueryGuardService" />
///     — this is a fact about the embedding window, not a togglable policy, so it runs independently
///     of <see cref="QueryGuardConfigKeys.EnabledGlobal" /> and its shadow mode.
///     <para>
///         Chars, not tokens: Core cannot reference the ONNX tokenizer (clean-layering), so this is a
///         proxy for the model's 254-token content window (<c>OnnxEmbeddingGenerator.MaxContentTokens</c>),
///         not an exact count. ~1,000 characters is the rule of thumb for English prose; the real
///         figure varies with content (a 12,952-character markdown table measured 249 tokens because
///         its whitespace padding collapses — ADR-0071).
///     </para>
/// </summary>
public static class QueryLengthGuard
{
    public const string WarnPolicyName = "QueryOverEmbeddingWindow";

    public const int WarnThresholdChars = 1000;

    public const string WarnGuidance =
        "This query is over 1,000 characters. Semantic search only embeds roughly the first 254 "
        + "tokens of a query (~1,000 characters of English prose, approximate) — search for the "
        + "identifying line instead of pasting the whole dump, e.g. the exception type, error code, "
        + "or failing test name. Keyword (FTS) matching still searches the query in full.";

    public static QueryGuardVerdict Evaluate(string query)
    {
        Guard.IsNotNull(query);
        return query.Length > WarnThresholdChars
            ? QueryGuardVerdict.Warn(WarnPolicyName, WarnGuidance)
            : QueryGuardVerdict.Clean;
    }
}
