using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup.Diagnostics;

/// <summary>
///     One corpus's engine wiring — the single place per-corpus facts live. Null keys mean the
///     corpus has no such axis: the code engine is always local, so its provider/baseUrl/apiKey
///     keys are null (SqliteCodeEngineStore.cs:51). Keyed on the solution's own <see cref="EmbedCorpus" />
///     so doctor and the drain relay cannot drift a second corpus list (R1 S1).
/// </summary>
internal sealed record CorpusEngineProbe(
    EmbedCorpus Corpus,
    string? ProviderKey,
    string ModelKey,
    string? BaseUrlKey,
    string? ApiKeyKey,
    string PendingTable,
    string PendingSql,
    string NotConfiguredRemedy)
{
    public string Label => Corpus.ToString().ToLowerInvariant();

    public static readonly CorpusEngineProbe Memory = new(
        EmbedCorpus.Memory,
        ProviderKey: EmbeddingSettingsKeys.Provider,
        ModelKey: EmbeddingSettingsKeys.Model,
        BaseUrlKey: EmbeddingSettingsKeys.BaseUrl,
        ApiKeyKey: EmbeddingSettingsKeys.ApiKey,
        PendingTable: "entries",
        PendingSql: MemorySql.CountPendingEmbed,
        NotConfiguredRemedy:
            $"not configured — run '{EmbeddingEngineSetup.DefaultModelCommand}' to enable semantic memory search");

    public static readonly CorpusEngineProbe Code = new(
        EmbedCorpus.Code,
        ProviderKey: null,
        ModelKey: EmbeddingSettingsKeys.CodeModel,
        BaseUrlKey: null,
        ApiKeyKey: null,
        PendingTable: "code_entries",
        PendingSql: MemorySql.CountPendingCodeEmbed,
        NotConfiguredRemedy:
            $"not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search");

    /// <summary>Both corpora in report order — the list the report iterates, so a third corpus is one descriptor, not a fourth copy of the grammar.</summary>
    public static readonly IReadOnlyList<CorpusEngineProbe> All = [Memory, Code];
}

/// <summary>
///     One corpus's diagnosed engine line, ready to render: the value, an optional parenthetical
///     detail, an optional em-dash suffix, and the pending count (null = the count is unreadable).
///     A null <see cref="Value" /> is "not configured"; <see cref="Unreadable" /> is the degraded
///     arm — the distinction P1 §1.3 requires so a failed read never prints a false remedy. The
///     pending count rides on the same state because it reads off its own table and survives a
///     broken settings read (R2 N4).
/// </summary>
internal sealed record CorpusEngineState(string? Value, string? Detail, string? Suffix, long? PendingRows, bool Unreadable = false);

/// <summary>
///     doctor's engine/pending line grammar — the single producer of both sentences (R1 M2:
///     one grammar, all four arms; the parenthetical and the em-dash clause render only when present).
/// </summary>
internal static class CorpusEngineLines
{
    internal static string EngineLine(CorpusEngineProbe probe, CorpusEngineState state) =>
        state.Unreadable
            ? $"{probe.Label} engine: unreadable (settings table missing or unreadable)"
            : state.Value is null
                ? $"{probe.Label} engine: {probe.NotConfiguredRemedy}"
                : $"{probe.Label} engine: {state.Value}{(state.Detail is null ? "" : $" ({state.Detail})")}{(state.Suffix is null ? "" : $" — {state.Suffix}")}";

    internal static string PendingLine(CorpusEngineProbe probe, CorpusEngineState state) =>
        $"{probe.Label} rows pending: {state.PendingRows?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}";
}
