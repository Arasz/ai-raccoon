using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Fixed-alpha fusion of content and structure similarities (see
///     docs/adr/0004-dual-vector-structure-signal.md):
///     score = alpha * sim(query, content) + (1 - alpha) * sim(query, structure).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StructureFusionTests
{
    [Fact]
    public void Fused_AlphaOne_ReturnsContentSim() => StructureFusion.Fused(0.8, 0.2, 1.0).ShouldBe(0.8, 1e-9);

    [Fact]
    public void Fused_AlphaZero_ReturnsStructureSim() => StructureFusion.Fused(0.8, 0.2, 0.0).ShouldBe(0.2, 1e-9);

    [Fact]
    public void Fused_AlphaHalf_BlendsEqually() => StructureFusion.Fused(0.8, 0.2, 0.5).ShouldBe(0.5, 1e-9);

    /// <summary>
    ///     Adjudicated, not transcribed (docs/adr/0057). 65.4% of the gate corpus and 64% of the live
    ///     bank have no structure embedding, so this default caps two thirds of every bank at alpha of
    ///     what a headed row can reach — which reads as a defect and was proposed as one (WP12).
    ///     Scoring absent structure as content-only instead was built and measured: S3 3→4, S4 3→6,
    ///     S6 3→10, A2 1→2, held-out A10 0.170→0.146. The cap is the mechanism, not a bug.
    /// </summary>
    [Fact]
    public void Fused_AbsentStructure_ContributesZero_WhichIsHowTheSignalFavoursHeadedChunks() =>
        StructureFusion.Fused(0.8, null, 0.5).ShouldBe(0.4, 1e-9);

    /// <summary>
    ///     The load-bearing property, pinned: a headed row outranks a headless row of equal content
    ///     similarity. Scoring absent structure as content-only makes these tie, and the section-targeted
    ///     gates that depend on the difference go red (docs/adr/0057).
    /// </summary>
    [Fact]
    public void Rank_HeadedRow_OutranksHeadlessRowOfEqualContentSimilarity()
    {
        var content = new[] { new VectorHit("headed", 0.6), new VectorHit("headless", 0.6) };
        var structure = new[] { new VectorHit("headed", 0.6) };

        var ranked = StructureFusion.Rank(content, structure, 0.5, 10);

        ranked.Select(r => r.Hash).ShouldBe(["headed", "headless"]);
        ranked[0].Score.ShouldBeGreaterThan(ranked[1].Score);
    }

    [Fact]
    public void Fused_AlphaOutOfRange_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StructureFusion.Fused(0.5, 0.5, 1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => StructureFusion.Fused(0.5, 0.5, -0.1));
    }

    [Fact]
    public void SimFromDistance_ConvertsCosineDistanceToSimilarity()
    {
        StructureFusion.SimFromDistance(0.0).ShouldBe(1.0, 1e-9);
        StructureFusion.SimFromDistance(1.0).ShouldBe(0.0, 1e-9);
        StructureFusion.SimFromDistance(2.0).ShouldBe(-1.0, 1e-9);
    }

    [Fact]
    public void Rank_AlphaOne_PreservesContentOrder()
    {
        var content = new[] { new VectorHit("b", 0.9), new VectorHit("a", 0.8) };
        var structure = new[] { new VectorHit("a", 0.99), new VectorHit("b", 0.1) };

        var ranked = StructureFusion.Rank(content, structure, 1.0, 10);

        ranked.Select(r => r.Hash).ShouldBe(["b", "a"]);
        ranked[0].Score.ShouldBe(0.9, 1e-9);
        ranked[1].Score.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void Rank_AlphaZero_PreservesStructureOrder()
    {
        var content = new[] { new VectorHit("b", 0.9), new VectorHit("a", 0.8) };
        var structure = new[] { new VectorHit("a", 0.99), new VectorHit("b", 0.1) };

        var ranked = StructureFusion.Rank(content, structure, 0.0, 10);

        ranked.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    /// <summary>
    ///     The asymmetry is deliberate (docs/adr/0057): every row has a content embedding, so missing
    ///     the content KNN window is a low similarity and stays worth zero. Only structure absence can
    ///     mean "never measured".
    /// </summary>
    [Fact]
    public void Rank_StructureOnlyCandidate_GetsZeroContentContribution()
    {
        var content = new[] { new VectorHit("a", 0.8) };
        var structure = new[] { new VectorHit("a", 0.5), new VectorHit("b", 0.9) };

        var ranked = StructureFusion.Rank(content, structure, 0.5, 10);

        // b: 0.5*0 + 0.5*0.9 = 0.45; a: 0.5*0.8 + 0.5*0.5 = 0.65.
        ranked.Select(r => r.Hash).ShouldBe(["a", "b"]);
        ranked[1].Score.ShouldBe(0.45, 1e-9);
    }

    [Fact]
    public void Rank_EqualScores_TieBreaksByHashOrdinal()
    {
        var content = new[]
        {
            new VectorHit("bb", 0.5),
            new VectorHit("aa", 0.5)
        };
        var structure = Array.Empty<VectorHit>();

        var ranked = StructureFusion.Rank(content, structure, 0.5, 10);

        // Both fused scores are equal; the tie-break must be deterministic (ordinal hash).
        ranked.Select(r => r.Hash).ShouldBe(["aa", "bb"]);
    }

    [Fact]
    public void Rank_Limit_Truncates()
    {
        var content = new[]
        {
            new VectorHit("a", 0.9),
            new VectorHit("b", 0.8),
            new VectorHit("c", 0.7)
        };

        var ranked = StructureFusion.Rank(content, [], 0.5, 2);

        ranked.Count.ShouldBe(2);
        ranked.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Rank_EmptyLists_ReturnsEmpty() =>
        StructureFusion.Rank([], [], 0.5, 10)
            .ShouldBeEmpty();

    [Fact]
    public void Rank_ZeroSimilarities_ProduceZeroScores()
    {
        var content = new[] { new VectorHit("a", 0.0) };
        var structure = new[] { new VectorHit("a", 0.0) };

        var ranked = StructureFusion.Rank(content, structure, 0.5, 10);

        ranked[0].Score.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void DefaultAlpha_IsTheDocumentedBlend() => SearchParameterSettingsKeys.DefaultStructureAlpha.ShouldBe(0.5);

    /// <summary>ADR-0108: granite scores unrelated text around 0.75, so ADR-0004's "no heading = 0"
    /// would sink a heading-less note under any headed chunk. Rescaling so the engine's relevance
    /// floor maps to 0 restores the scale that rule was measured on.</summary>
    [Fact]
    public void Rank_WithTheEnginesFloor_KeepsAHeadinglessNoteAboveUnrelatedHeadedChunks()
    {
        var ranked = StructureFusion.Rank(
            [new VectorHit("note", 0.959), new VectorHit("rollback", 0.757)],
            [new VectorHit("rollback", 0.80)],
            alpha: 0.5, limit: 10, similarityFloor: 0.79);

        ranked[0].Hash.ShouldBe("note");
    }

    /// <summary>Rows the floor clamps to 0 keep their content-similarity order. A hash carries the
    /// row's path, so ordering them by hash reorders the same bank under another directory.</summary>
    [Fact]
    public void Rank_RowsTheFloorClampsToZero_KeepTheirContentSimilarityOrder()
    {
        var ranked = StructureFusion.Rank(
            [new VectorHit("aa", 0.72), new VectorHit("bb", 0.74)],
            [new VectorHit("aa", 0.75)],
            alpha: 0.5, limit: 10, similarityFloor: 0.79);

        ranked.Select(r => r.Score).ShouldAllBe(score => score == 0.0, "premise: the floor clamps both rows to 0");
        ranked.Select(r => r.Hash).ShouldBe(["bb", "aa"]);
    }

    [Fact]
    public void Rank_WithoutAFloor_IsUnchanged()
    {
        var withDefault = StructureFusion.Rank([new VectorHit("a", 0.6)], [new VectorHit("a", 0.4)], 0.5, 10);
        var withZero = StructureFusion.Rank([new VectorHit("a", 0.6)], [new VectorHit("a", 0.4)], 0.5, 10, similarityFloor: 0.0);

        withDefault.Single().Score.ShouldBe(0.5, tolerance: 1e-12);
        withZero.Single().Score.ShouldBe(0.5, tolerance: 1e-12);
    }
}
