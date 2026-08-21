namespace AiRaccoon.Core.Memory.Code;

/// <summary>Constant warning text for the code section (§3.3/§3.6).</summary>
public static class CodeSearchWarnings
{
    /// <summary>No embedding.codeModel configured: code search degrades to FTS5-only and says so.</summary>
    public const string EngineNotConfigured = "code engine not configured — FTS5-only results";

    /// <summary>
    ///     The query exceeded the configured code engine's manifest window (126 tokens for
    ///     code-daemon-embed-v1) and was trimmed before embedding — the vector leg saw only the
    ///     trimmed prefix; the FTS5 leg still saw the query in full (§12.6: "code-budget warning
    ///     belongs to WP5").
    /// </summary>
    public const string QueryTrimmedToCodeWindow =
        "code search query was shortened to fit the code embedding model's window — the semantic " +
        "match saw only the first part of the query; keyword matching still saw it in full.";
}
