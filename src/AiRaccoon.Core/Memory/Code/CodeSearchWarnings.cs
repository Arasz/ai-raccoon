namespace AiRaccoon.Core.Memory.Code;

/// <summary>Constant warning text for the code section (§3.3/§3.6).</summary>
public static class CodeSearchWarnings
{
    /// <summary>
    ///     No embedding.codeModel configured: code search degrades to FTS5-only, says so, and names
    ///     the one command that fixes it (#422) — a caller that cannot act on the warning will keep
    ///     reading keyword-only code hits as if they were the whole answer.
    /// </summary>
    /// <summary>The part a caller matches on; the rest of the warning is advice that may be reworded.</summary>
    public const string EngineNotConfiguredPrefix = "code engine not configured";

    /// <inheritdoc cref="EngineNotConfiguredPrefix" />
    public const string EngineNotConfigured =
        EngineNotConfiguredPrefix + " — FTS5-only results; run '" + CodeEngineSetup.DefaultModelCommand
        + "' to download and activate the default code embedding model";

    /// <summary>
    ///     The query exceeded the configured code engine's manifest window (510 tokens for
    ///     code-daemon-embed-v1) and was trimmed before embedding — the vector leg saw only the
    ///     trimmed prefix; the FTS5 leg still saw the query in full (§12.6: "code-budget warning
    ///     belongs to WP5").
    /// </summary>
    public const string QueryTrimmedToCodeWindow =
        "code search query was shortened to fit the code embedding model's window — the semantic " +
        "match saw only the first part of the query; keyword matching still saw it in full.";
}
