namespace AiRaccoon.Core.Memory;

/// <summary>The bank's committed contexts plus entry/pending counts (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_stats).</summary>
public sealed record MemoryStats(int EntryCount, int PendingCount, IReadOnlyList<string> Contexts);
