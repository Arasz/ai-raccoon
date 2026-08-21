namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     Constant warning text for the code section (§3.3: with no embedding.codeModel configured —
///     always, in this wave — code search degrades to FTS5-only and says so).
/// </summary>
public static class CodeSearchWarnings
{
    public const string EngineNotConfigured = "code engine not configured — FTS5-only results";
}
