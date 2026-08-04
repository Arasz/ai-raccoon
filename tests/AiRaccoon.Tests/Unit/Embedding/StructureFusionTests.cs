using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Fixed-alpha fusion of content and structure similarities (plan C Wave 6):
///     score = alpha * sim(query, content) + (1 - alpha) * sim(query, structure).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StructureFusionTests
{
    [Fact]
    public void Fused_AlphaOne_ReturnsContentSim()
    {
        StructureFusion.Fused(0.8, 0.2, alpha: 1.0).ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void Fused_AlphaZero_ReturnsStructureSim()
    {
        StructureFusion.Fused(0.8, 0.2, alpha: 0.0).ShouldBe(0.2, 1e-9);
    }

    [Fact]
    public void Fused_AlphaHalf_BlendsEqually()
    {
        StructureFusion.Fused(0.8, 0.2, alpha: 0.5).ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Fused_MissingStructure_ContributesZero()
    {
        StructureFusion.Fused(0.8, null, alpha: 0.5).ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void Fused_AlphaOutOfRange_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StructureFusion.Fused(0.5, 0.5, alpha: 1.5));
        Should.Throw<ArgumentOutOfRangeException>(() => StructureFusion.Fused(0.5, 0.5, alpha: -0.1));
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

        var ranked = StructureFusion.Rank(content, structure, alpha: 1.0, limit: 10);

        ranked.Select(r => r.Hash).ShouldBe(["b", "a"]);
        ranked[0].Score.ShouldBe(0.9, 1e-9);
        ranked[1].Score.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public void Rank_AlphaZero_PreservesStructureOrder()
    {
        var content = new[] { new VectorHit("b", 0.9), new VectorHit("a", 0.8) };
        var structure = new[] { new VectorHit("a", 0.99), new VectorHit("b", 0.1) };

        var ranked = StructureFusion.Rank(content, structure, alpha: 0.0, limit: 10);

        ranked.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Rank_StructureOnlyCandidate_GetsZeroContentContribution()
    {
        var content = new[] { new VectorHit("a", 0.8) };
        var structure = new[] { new VectorHit("a", 0.5), new VectorHit("b", 0.9) };

        var ranked = StructureFusion.Rank(content, structure, alpha: 0.5, limit: 10);

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

        var ranked = StructureFusion.Rank(content, structure, alpha: 0.5, limit: 10);

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

        var ranked = StructureFusion.Rank(content, Array.Empty<VectorHit>(), alpha: 0.5, limit: 2);

        ranked.Count.ShouldBe(2);
        ranked.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Rank_EmptyLists_ReturnsEmpty()
    {
        StructureFusion.Rank(Array.Empty<VectorHit>(), Array.Empty<VectorHit>(), alpha: 0.5, limit: 10)
            .ShouldBeEmpty();
    }

    [Fact]
    public void Rank_ZeroSimilarities_ProduceZeroScores()
    {
        var content = new[] { new VectorHit("a", 0.0) };
        var structure = new[] { new VectorHit("a", 0.0) };

        var ranked = StructureFusion.Rank(content, structure, alpha: 0.5, limit: 10);

        ranked[0].Score.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void DefaultAlpha_IsTheDocumentedBlend()
    {
        StructureFusion.DefaultAlpha.ShouldBe(0.5);
    }
}
