namespace AiRaccon.Core.Memory;

/// <summary>The bank's committed contexts plus entry/pending counts (spec §4.1 memory_stats).</summary>
public sealed record MemoryStats(int EntryCount, int PendingCount, IReadOnlyList<string> Contexts);
