namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Which bank-wide drain to run (docs/work/2026-08-22-post-delta-3-plan.md WP11-B2). Both
///     <see cref="IEntryEmbedder.EmbedPendingBatchAsync" /> and
///     <see cref="ICodeEmbedder.EmbedPendingBatchAsync" /> are bank-wide, not project-scoped, so the
///     item space is exactly these two values — no project id.
/// </summary>
public enum EmbedCorpus
{
    Memory,
    Code
}

/// <summary>
///     One embed-topic pump item: "drain <see cref="Corpus" />'s pending rows". Coalescing key is
///     the record's own structural equality — two signals for the same corpus queued before either
///     is taken collapse to one <see cref="AiRaccoon.Core.EventPump.EventPump{T}" /> entry.
/// </summary>
public sealed record EmbedDrainRequest(EmbedCorpus Corpus);
