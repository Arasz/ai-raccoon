using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     `Merge` is handed an already-fused list and fuses it a second time, rebuilding every score
///     from rank position and discarding the modality scores. These tests pin that behaviour
///     exactly, as a known defect the corpus cannot yet adjudicate a fix for (docs/adr/0058).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchResultMergerTests
{
    /// <summary>The closed form the second fusion produces at the default k: (k+1)/(k+rank).</summary>
    private static double Positional(int rank) =>
        (SearchQuery.DefaultRrfK + 1.0) / (SearchQuery.DefaultRrfK + rank);

    private static MemorySearchResult Hit(string hash, double ranking) => new(hash, ranking, $"{hash}.md", "s");

    /// <summary>
    ///     KNOWN DEFECT, pinned rather than asserted as a contract (H1 of the 2026-08-14 project-scope
    ///     review). A strong match set and near-orthogonal junk arrive with different fused scores and
    ///     leave with identical ones, because the second fusion rebuilds both from rank position. When
    ///     the redundant pass is removed this test FAILS, and that failure is the signal to delete it
    ///     and assert the fused scores instead — see docs/adr/0058 for why it has not been removed yet.
    ///     Do not "fix" it by widening the comparison.
    /// </summary>
    [Fact]
    public void Merge_RebuildsScoresFromRankPosition_DiscardingTheFusedScores()
    {
        var strong = new[] { Hit("a", 1.0), Hit("b", 0.98), Hit("c", 0.95) };
        var junk = new[] { Hit("a", 1.0), Hit("b", 0.31), Hit("c", 0.12) };
        double[] positional = [Positional(1), Positional(2), Positional(3)];

        var mergedStrong = SearchResultMerger.Merge([strong], 3);
        var mergedJunk = SearchResultMerger.Merge([junk], 3);

        mergedStrong.Select(result => result.Ranking).ShouldBe(positional, 1e-12,
            "a strong candidate set is rescored to the closed form (k+1)/(k+rank)");
        mergedJunk.Select(result => result.Ranking).ShouldBe(positional, 1e-12,
            "and so is near-orthogonal junk — the two curves are byte-identical");
    }

    /// <summary>
    ///     The second fusion is order-preserving, which is why the defect has never shown up as a
    ///     ranking bug: (k+1)/(k+rank) is strictly decreasing in rank, so re-fusing a sorted list
    ///     returns it in the same order. Only the numbers are lost. This is the measured basis for
    ///     ADR-0058's finding that removing the pass changes nothing at λ=0.
    /// </summary>
    [Fact]
    public void Merge_SecondFusion_PreservesTheIncomingOrder()
    {
        var candidates = new[] { Hit("a", 1.0), Hit("b", 0.31), Hit("c", 0.12) };

        var merged = SearchResultMerger.Merge([candidates], 10);

        merged.Select(result => result.Hash).ShouldBe(["a", "b", "c"]);
    }

    /// <summary>
    ///     What the floor actually filters. `minRelativeScore` is relative to the top hit
    ///     (docs/adr/0047) but compares against the positional curve, so 0.9 admits every rank up to
    ///     7 regardless of how badly those rows matched.
    /// </summary>
    [Fact]
    public void Merge_FloorComparesAgainstThePositionalCurve_NotMatchQuality()
    {
        var candidates = Enumerable.Range(1, 10).Select(i => Hit($"h{i:D2}", 1.0 / i)).ToArray();

        var merged = SearchResultMerger.Merge([candidates], 20, minRelativeScore: 0.9);

        merged.Count.ShouldBe(7, "ranks 1-7 clear 0.9 on the positional curve; rank 8 is 0.8971");
        Positional(7).ShouldBeGreaterThanOrEqualTo(0.9);
        Positional(8).ShouldBeLessThan(0.9);
    }
}
