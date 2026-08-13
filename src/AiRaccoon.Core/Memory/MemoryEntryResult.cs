namespace AiRaccoon.Core.Memory;

/// <summary>
///     The entry as stored, plus whether this call actually created it (vs. returned an
///     existing row). Created comes from the insert's change count: the ON CONFLICT DO NOTHING
///     loser reports false — the only formula that makes "promotedHashes contains only
///     actually-created rows" hold for concurrent racers.
/// </summary>
public sealed record MemoryEntryResult(MemoryEntry Entry, bool Created);
