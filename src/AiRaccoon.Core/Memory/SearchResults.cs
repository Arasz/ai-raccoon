namespace AiRaccoon.Core.Memory;

/// <summary>
///     The envelope <see cref="IMemoryStore.SearchAsync" /> returns (owner ruling; WP1 wires it).
///     <see cref="Timings" /> rides out with the results so the host can tag and record them
///     without a side channel back into the store.
/// </summary>
public sealed record SearchResults(IReadOnlyList<MemorySearchResult> Results, SearchTimings Timings);

/// <summary>Per-phase durations for one <see cref="IMemoryStore.SearchAsync" /> call.</summary>
public sealed record SearchTimings(
    TimeSpan Fts,
    TimeSpan Vector,
    TimeSpan Fusion,
    TimeSpan Affinity,
    TimeSpan Snippets,
    TimeSpan Bump)
{
    /// <summary>All-zero timings, for a caller that has not measured phases yet (WP1, before WP2 lands).</summary>
    public static SearchTimings Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
}
