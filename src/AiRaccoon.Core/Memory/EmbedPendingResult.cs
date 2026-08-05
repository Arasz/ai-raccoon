namespace AiRaccoon.Core.Memory;

/// <summary>Outcome of a deferred-embedding batch (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_embed_pending).</summary>
public sealed record EmbedPendingResult(int Processed, int Pending);
