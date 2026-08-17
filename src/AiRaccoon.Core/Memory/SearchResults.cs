using AiRaccoon.Core.Memory.Fusion;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The envelope <see cref="IMemoryStore.SearchAsync" /> returns (owner ruling; WP1 wires it).
///     <see cref="Timings" /> rides out with the results so the host can tag and record them
///     without a side channel back into the store. <see cref="Fusion" /> is null on every default
///     search — it is set only when the no-fusion-regression flag is on (docs/adr/0078).
/// </summary>
public sealed record SearchResults(
    IReadOnlyList<MemorySearchResult> Results,
    SearchTimings Timings,
    FusionDiff? Fusion = null);

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

    /// <summary>
    ///     The metric names a search records — `search.&lt;phase&gt;`, one per constructor parameter
    ///     above, in declaration order. Declared explicitly, not derived by reflecting over "every
    ///     TimeSpan property" (F11): that would have silently minted a series for any future computed
    ///     property (e.g. a <c>Total</c>). Adding a seventh phase means adding it here <em>and</em> to
    ///     the test that pins these six names (SearchResultsTests) — deliberate, not an oversight.
    /// </summary>
    public static IReadOnlyList<string> PhaseNames { get; } =
        ["search.fts", "search.vector", "search.fusion", "search.affinity", "search.snippets", "search.bump"];

    /// <summary>This instance's phases as name/value pairs, in the same order as <see cref="PhaseNames" />.</summary>
    public IReadOnlyList<(string Name, TimeSpan Value)> Phases() =>
        [
            (PhaseNames[0], Fts),
            (PhaseNames[1], Vector),
            (PhaseNames[2], Fusion),
            (PhaseNames[3], Affinity),
            (PhaseNames[4], Snippets),
            (PhaseNames[5], Bump)
        ];
}
