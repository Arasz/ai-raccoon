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
    FusionDiff? Fusion = null,
    IReadOnlyDictionary<string, RetrievalEvidence>? EvidenceByHash = null,
    FusionStats? Stats = null);

/// <summary>Per-phase durations for one <see cref="IMemoryStore.SearchAsync" /> call, plus the measured total (docs/adr/0079).</summary>
public sealed record SearchTimings(
    TimeSpan Open,
    TimeSpan Embed,
    TimeSpan Fts,
    TimeSpan Vector,
    TimeSpan Fusion,
    TimeSpan Merge,
    TimeSpan Adjustment,
    TimeSpan Snippets,
    TimeSpan Bump,
    TimeSpan Total)
{
    /// <summary>
    ///     The metric name <see cref="Total" /> records under. Not in <see cref="PhaseNames" /> (F11):
    ///     it is a measured member of this record, not a decomposition entry, so a consumer that ever
    ///     sums <see cref="PhaseNames" /> can never double-count it.
    /// </summary>
    public const string TotalName = "search.total";

    /// <summary>All-zero timings, for a caller that has not measured phases yet (WP1, before WP2 lands).</summary>
    public static SearchTimings Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    ///     The metric names a search records — `search.&lt;phase&gt;`, one per constructor parameter
    ///     above except <see cref="Total" />, in declaration order. Declared explicitly, not derived
    ///     by reflecting over "every TimeSpan property" (F11): that would have silently minted a
    ///     series for <see cref="Total" /> too. Adding a phase means adding it here <em>and</em> to
    ///     the test that pins these names (SearchResultsTests) — deliberate, not an oversight.
    ///     <c>search.affinity</c> stays bound to <see cref="Merge" /> rather than being renamed
    ///     (#465): renaming an exported series would orphan its stored metric rows.
    /// </summary>
    public static IReadOnlyList<string> PhaseNames { get; } =
    [
        "search.open", "search.embed", "search.fts", "search.vector",
        "search.fusion", "search.affinity", "search.adjustment", "search.snippets", "search.bump"
    ];

    /// <summary>Every series a search records — <see cref="PhaseNames" /> plus <see cref="TotalName" /> — derived once, so downstream readers never keep a second hand-written copy (derive-or-delete-the-list).</summary>
    public static IReadOnlyList<string> SeriesNames { get; } = [.. PhaseNames, TotalName];

    /// <summary>This instance's phases as name/value pairs, in the same order as <see cref="PhaseNames" />.</summary>
    public IReadOnlyList<(string Name, TimeSpan Value)> Phases() =>
    [
        (PhaseNames[0], Open),
        (PhaseNames[1], Embed),
        (PhaseNames[2], Fts),
        (PhaseNames[3], Vector),
        (PhaseNames[4], Fusion),
        (PhaseNames[5], Merge),
        (PhaseNames[6], Adjustment),
        (PhaseNames[7], Snippets),
        (PhaseNames[8], Bump)
    ];

    /// <summary>This instance's series as name/value pairs — <see cref="Phases" /> plus <see cref="Total" /> under <see cref="TotalName" /> — mirroring <see cref="FusionDiff.Measurements" />.</summary>
    public IReadOnlyList<(string Name, TimeSpan Value)> Measurements() => [.. Phases(), (TotalName, Total)];
}
