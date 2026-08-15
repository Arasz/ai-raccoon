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
            TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6));

        var searchResults = new SearchResults(results, timings);

        searchResults.Results.ShouldBe(results);
        searchResults.Timings.ShouldBe(timings);
    }

    /// <summary>WP1 returns `new SearchResults(merged, SearchTimings.Empty)` before phase timing lands (WP2).</summary>
    [Fact]
    public void Empty_IsAllZeroDurations()
    {
        var empty = SearchTimings.Empty;

        empty.Fts.ShouldBe(TimeSpan.Zero);
        empty.Vector.ShouldBe(TimeSpan.Zero);
        empty.Fusion.ShouldBe(TimeSpan.Zero);
        empty.Affinity.ShouldBe(TimeSpan.Zero);
        empty.Snippets.ShouldBe(TimeSpan.Zero);
        empty.Bump.ShouldBe(TimeSpan.Zero);
    }
}
