using AiRaccoon.Core.Memory.QueryGuard;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     Combines the tiered read-path guard's verdict (docs/adr/0040) with the always-on query-length
///     verdict (docs/adr/0071) and each corpus's engine-degradation note into memory_search's one
///     caller-visible warning string.
/// </summary>
public static class SearchWarnings
{
    /// <summary>
    ///     No `embedding.provider` configured: memory search degrades to FTS5-only, says so, and
    ///     names the one command that fixes it. The memory twin of
    ///     <see cref="Code.CodeSearchWarnings.EngineNotConfiguredPrefix" /> — a caller that cannot
    ///     act on the warning will keep reading keyword-only memory hits as if they were the whole
    ///     answer (F6).
    /// </summary>
    public const string EngineNotConfiguredPrefix = "memory engine not configured";

    /// <inheritdoc cref="EngineNotConfiguredPrefix" />
    public const string EngineNotConfigured =
        EngineNotConfiguredPrefix + " — the memory section is FTS5-only; run '" + EmbeddingEngineSetup.DefaultModelCommand
        + "' to download and activate the default memory embedding model";

    /// <summary>
    ///     Joins every supplied note in order, skipping nulls: guard guidance, length guidance,
    ///     the memory leg's engine note, then the code leg's. Null when nothing warned.
    /// </summary>
    public static string? Compose(QueryGuardVerdict guardVerdict, QueryGuardVerdict lengthVerdict,
        string? memoryEngineWarning = null, string? codeEngineWarning = null)
    {
        List<string>? warnings = null;
        Collect(guardVerdict, ref warnings);
        Collect(lengthVerdict, ref warnings);
        Collect(memoryEngineWarning, ref warnings);
        Collect(codeEngineWarning, ref warnings);
        return warnings is null ? null : string.Join(" ", warnings);
    }

    /// <summary>
    ///     The memory leg's engine note: null when a provider is configured, the remedy-naming
    ///     warning when it is absent — the same settings row doctor reads for the memory engine.
    /// </summary>
    public static string? MemoryEngineWarning(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? EngineNotConfigured : null;

    private static void Collect(QueryGuardVerdict verdict, ref List<string>? warnings)
    {
        if (verdict.Tier != QueryGuardTier.Warn)
        {
            return;
        }

        (warnings ??= []).Add(verdict.Guidance!);
    }

    private static void Collect(string? warning, ref List<string>? warnings)
    {
        if (warning is not null)
        {
            (warnings ??= []).Add(warning);
        }
    }
}
