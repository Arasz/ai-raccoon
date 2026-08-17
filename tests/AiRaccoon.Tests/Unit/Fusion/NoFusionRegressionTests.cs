using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     The no-fusion-regression reorder (docs/adr/0078): ADR-0006 declares the hybrid never ranks
///     the expected chunk below the best single modality, and issue #367 is the real bank's
///     counterexample. These pin the rule as an ORDER, which is all that survives the second
///     fusion in <see cref="AiRaccoon.Infrastructure.Sqlite.SearchResultMerger" /> (ADR-0058).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoFusionRegressionTests
{
    private static MemorySearchResult Hit(string hash) => new(hash, 0, hash, "snippet");

    private static IReadOnlyList<MemorySearchResult> Hits(params string[] hashes) => [.. hashes.Select(Hit)];

    private static ModalityLeg Leg(string name, params string[] hashes) => ModalityLeg.From(name, Hits(hashes));

    /// <summary>
    ///     Issue #367's shape: the target is rank 1 on one leg and unseen by the other, and the
    ///     fusion buries it under consensus pairs. The rule ADR-0006 already claims says it may not
    ///     rank below where its best single leg put it.
    /// </summary>
    [Fact]
    public void Reorder_TargetRankedFirstByOneLeg_AndUnseenByTheOther_RisesToTheTop()
    {
        var fts = Leg("fts", "target", "c1", "c2", "c3");
        var vector = Leg("vector", "c1", "c2", "c3");
        // Consensus pairs outscore a single rank-1: c1 = 1/61 + 1/61 beats target = 1/61.
        var fused = Hits("c1", "c2", "c3", "target");

        var reordered = NoFusionRegression.Reorder(fused, [fts, vector]);

        reordered.Select(r => r.Hash).ShouldBe(["c1", "target", "c2", "c3"]);
    }

    /// <summary>Every result keeps a distinct position, so no downstream tie-break on Path can decide the top hit.</summary>
    [Fact]
    public void Reorder_ProducesAStrictTotalOrder_WithEveryInputExactlyOnce()
    {
        var fts = Leg("fts", "target", "c1", "c2", "c3");
        var vector = Leg("vector", "c1", "c2", "c3");
        var fused = Hits("c1", "c2", "c3", "target");

        var reordered = NoFusionRegression.Reorder(fused, [fts, vector]);

        reordered.Select(r => r.Hash).ShouldBeUnique();
        reordered.Select(r => r.Hash).Order().ShouldBe(fused.Select(r => r.Hash).Order());
    }

    /// <summary>
    ///     A leg that never ran has no opinion, so it cannot be read as disagreement. With one
    ///     contributing leg the rule is the identity — proved here rather than assumed, because the
    ///     opposed gate (the reorder fires on #367) is otherwise satisfied by firing on everything.
    /// </summary>
    [Fact]
    public void Reorder_VectorLegSkipped_ReturnsTheFusedOrderUnchanged()
    {
        var fts = Leg("fts", "a", "b", "c");
        var vector = ModalityLeg.Skipped("vector");
        var fused = Hits("a", "b", "c");

        var reordered = NoFusionRegression.Reorder(fused, [fts, vector]);

        reordered.Select(r => r.Hash).ShouldBe(["a", "b", "c"]);
    }

    /// <summary>A leg that ran and matched nothing is degradation too, not a vote against every result.</summary>
    [Fact]
    public void Reorder_LegRanButReturnedNothing_DoesNotContribute()
    {
        var fts = Leg("fts", "a", "b", "c");
        var vector = ModalityLeg.From("vector", Hits());

        vector.Contributes.ShouldBeFalse();
        NoFusionRegression.Reorder(Hits("a", "b", "c"), [fts, vector])
            .Select(r => r.Hash).ShouldBe(["a", "b", "c"]);
    }

    /// <summary>
    ///     Absent from a leg's candidate window and ranked badly inside it are different signals, and
    ///     neither is a penalty: absence means the leg never saw the result, a bad rank means it saw
    ///     it and disagreed. Turning either into a demotion is the defect this pins.
    /// </summary>
    [Fact]
    public void Reorder_AbsentFromAWindow_AndRankedPoorlyInIt_AreBothNeutral()
    {
        // "absent" is outside the vector window entirely; "poor" sits at vector rank 4. The fused
        // order is what RRF actually produces from these two legs at k = 60, weights 1:1.
        var fts = Leg("fts", "top", "absent", "poor");
        var vector = Leg("vector", "top", "v1", "v2", "poor");
        var fused = Hits("top", "poor", "absent", "v1", "v2");

        var reordered = NoFusionRegression.Reorder(fused, [fts, vector]);

        reordered.Select(r => r.Hash).ShouldBe(["top", "poor", "absent", "v1", "v2"]);
    }

    /// <summary>The leg-availability record must be able to say which of the two happened.</summary>
    [Fact]
    public void ModalityLeg_DistinguishesSkippedFromRanAndReturnedCandidates()
    {
        ModalityLeg.Skipped("vector").Queried.ShouldBeFalse();
        ModalityLeg.From("vector", Hits()).Queried.ShouldBeTrue();
        ModalityLeg.From("vector", Hits()).Contributes.ShouldBeFalse();
        ModalityLeg.From("vector", Hits("a")).Contributes.ShouldBeTrue();
    }

    [Fact]
    public void Reorder_EmptyFusedList_IsReturnedUnchanged()
    {
        NoFusionRegression.Reorder([], [Leg("fts", "a"), Leg("vector", "a")]).ShouldBeEmpty();
    }

    /// <summary>The rule has no tunable constant — nothing here is a number chosen from a sweep.</summary>
    [Fact]
    public void Reorder_WhenBothLegsAgreeWithTheFusion_ChangesNothing()
    {
        var fts = Leg("fts", "a", "b", "c");
        var vector = Leg("vector", "a", "b", "c");

        NoFusionRegression.Reorder(Hits("a", "b", "c"), [fts, vector])
            .Select(r => r.Hash).ShouldBe(["a", "b", "c"]);
    }
}
