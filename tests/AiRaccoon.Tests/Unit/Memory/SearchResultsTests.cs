using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The envelope WP1 wires <see cref="IMemoryStore.SearchAsync" /> to return (owner ruling,
///     docs/plans/2026-08-15-performance-metrics-implementation.md, finding C). WP0 defines the
///     shape only; nothing here wires it into the store yet.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchResultsTests
{
    [Fact]
    public void Constructor_KeepsResultsAndTimings()
    {
        var results = new List<MemorySearchResult> { new("h1", 1.0, "p.md", "snippet") };
        var timings = new SearchTimings(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6),
            TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(9),
            TimeSpan.FromMilliseconds(10));

        var searchResults = new SearchResults(results, timings);

        searchResults.Results.ShouldBe(results);
        searchResults.Timings.ShouldBe(timings);
    }

    /// <summary>WP1 returns `new SearchResults(merged, SearchTimings.Empty)` before phase timing lands (WP2).</summary>
    [Fact]
    public void Empty_IsAllZeroDurations()
    {
        var empty = SearchTimings.Empty;

        empty.Open.ShouldBe(TimeSpan.Zero);
        empty.Embed.ShouldBe(TimeSpan.Zero);
        empty.Fts.ShouldBe(TimeSpan.Zero);
        empty.Vector.ShouldBe(TimeSpan.Zero);
        empty.Fusion.ShouldBe(TimeSpan.Zero);
        empty.Merge.ShouldBe(TimeSpan.Zero);
        empty.Adjustment.ShouldBe(TimeSpan.Zero);
        empty.Snippets.ShouldBe(TimeSpan.Zero);
        empty.Bump.ShouldBe(TimeSpan.Zero);
        empty.Total.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>
    ///     F11 (owner ruling): SearchTimings.PhaseNames is an explicit declaration, not reflection
    ///     over TimeSpan properties — a ninth phase means editing PhaseNames/Phases() AND this
    ///     literal, deliberately. Total is deliberately excluded (docs/plans/2026-08-17-search-phase-attribution.md
    ///     §2.2): it is a measured member of the record, not a decomposition entry, so summing
    ///     PhaseNames can never double-count it.
    /// </summary>
    [Fact]
    public void PhaseNames_OneEntryPerDecomposedPhase_PrefixedWithSearch()
    {
        SearchTimings.PhaseNames.ShouldBe(
        [
            "search.open", "search.embed", "search.fts", "search.vector",
            "search.fusion", "search.affinity", "search.snippets", "search.bump"
        ]);
    }

    [Fact]
    public void TotalName_IsSearchTotal()
    {
        SearchTimings.TotalName.ShouldBe("search.total");
    }

    /// <summary>Derived, not a second hand-kept list (derive-or-delete-the-list) — this is the one place PhaseNames and TotalName combine.</summary>
    [Fact]
    public void SeriesNames_IsPhaseNamesPlusTotalName()
    {
        SearchTimings.SeriesNames.ShouldBe([.. SearchTimings.PhaseNames, SearchTimings.TotalName]);
    }

    [Fact]
    public void Phases_PairsEachDerivedNameWithItsOwnValue()
    {
        var timings = new SearchTimings(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6),
            TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(9),
            TimeSpan.FromMilliseconds(10));

        var phases = timings.Phases();

        phases.ShouldBe(
        [
            ("search.open", TimeSpan.FromMilliseconds(1)),
            ("search.embed", TimeSpan.FromMilliseconds(2)),
            ("search.fts", TimeSpan.FromMilliseconds(3)),
            ("search.vector", TimeSpan.FromMilliseconds(4)),
            ("search.fusion", TimeSpan.FromMilliseconds(5)),
            ("search.affinity", TimeSpan.FromMilliseconds(6)),
            ("search.snippets", TimeSpan.FromMilliseconds(8)),
            ("search.bump", TimeSpan.FromMilliseconds(9))
        ]);
    }

    /// <summary>Mirrors FusionDiff.Measurements(): Phases() plus the measured Total, in that order.</summary>
    [Fact]
    public void Measurements_IsPhasesPlusTotal()
    {
        var timings = new SearchTimings(
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6),
            TimeSpan.FromMilliseconds(7), TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(9),
            TimeSpan.FromMilliseconds(10));

        timings.Measurements().ShouldBe([.. timings.Phases(), ("search.total", TimeSpan.FromMilliseconds(10))]);
    }
}
