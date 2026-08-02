namespace AiRaccoon.Core.Memory;

/// <summary>Outcome of a deferred-embedding batch (spec §4.1 memory_embed_pending).</summary>
public sealed record EmbedPendingResult(int Processed, int Pending);
