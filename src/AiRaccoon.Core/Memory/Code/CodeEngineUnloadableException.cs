namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     A configured code engine (embedding.codeModel) exists but its manifest or model/tokenizer
///     files failed to load — missing files, a dimension mismatch, or a corrupt asset. Distinct
///     from "no engine configured" (which degrades to FTS5-only silently): this is a genuine
///     misconfiguration, so code searches refuse with an actionable message instead of degrading.
///     Memory searches are unaffected — the memory and code engines are independent settings rows.
/// </summary>
public sealed class CodeEngineUnloadableException(string codeModel, Exception inner)
    : InvalidOperationException(
        $"The configured code engine at '{codeModel}' could not be loaded: {inner.Message} " +
        "Run 'ai-raccoon model code set local <dir>' to reconfigure it, or clear it with " +
        "'ai-raccoon settings model code reset'.", inner);
