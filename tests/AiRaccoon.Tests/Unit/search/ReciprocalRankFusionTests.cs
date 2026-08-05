using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReciprocalRankFusionTests
{
    private static MemorySearchResult Hit(string hash, string path = "") => new(hash, 0, 0, string.IsNullOrEmpty(path) ? hash : path, "snippet");

    [Fact]
    public void Fuse_Weights2To1_FavoursTheKeywordRankedList()
    {
        var fts = new[] { Hit("a"), Hit("b") };
        var vec = new[] { Hit("b"), Hit("a") };

        var fused = ReciprocalRankFusion.Fuse(
            [(fts, 2), (vec, 1)], 30, 0, 10);

        // a = 2/31 + 1/32 beats b = 2/32 + 1/31 when the keyword weight dominates.
        fused.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Fuse_Weights1To2_FavoursTheVectorRankedList()
    {
        var fts = new[] { Hit("a"), Hit("b") };
        var vec = new[] { Hit("b"), Hit("a") };

        var fused = ReciprocalRankFusion.Fuse(
            [(fts, 1), (vec, 2)], 30, 0, 10);

        fused.Select(r => r.Hash).ShouldBe(["b", "a"]);
    }

    [Fact]
    public void Fuse_NormalizesToMax_SoTheTopResultIsOne()
    {
        var fts = new[] { Hit("a"), Hit("b") };

        var fused = ReciprocalRankFusion.Fuse([(fts, 1)], 60, 0, 10);

        fused[0].Ranking.ShouldBe(1.0);
        fused[0].Hash.ShouldBe("a");
        fused[1].Ranking.ShouldBe(61.0 / 62, 1e-9);
    }

    [Fact]
    public void Fuse_CutoffK_ChangesTheScoreGapBetweenRanks()
    {
        var fts = new[] { Hit("a"), Hit("b") };

        var k10 = ReciprocalRankFusion.Fuse([(fts, 1)], 10, 0, 10);
        var k60 = ReciprocalRankFusion.Fuse([(fts, 1)], 60, 0, 10);

        k10[1].Ranking.ShouldBe(11.0 / 12, 1e-9);
        k60[1].Ranking.ShouldBe(61.0 / 62, 1e-9);
        k10[1].Ranking.ShouldBeLessThan(k60[1].Ranking);
    }

    [Fact]
    public void Fuse_EmptyModalityList_DoesNotEmptyTheResult()
    {
        // COALESCE: a doc is scored by whichever list retrieved it; an absent modality
        // (empty vec list) must not drop the keyword hits.
        var fts = new[] { Hit("a"), Hit("b") };

        var fused = ReciprocalRankFusion.Fuse([(fts, 1), ([], 1)],
            60, 0, 10);

        fused.Select(r => r.Hash).ShouldBe(["a", "b"]);
        fused[0].Ranking.ShouldBe(1.0);
    }

    [Fact]
    public void Fuse_AppliesMinScoreAfterNormalization()
    {
        var list = new[] { Hit("a"), Hit("b"), Hit("c") };

        var fused = ReciprocalRankFusion.Fuse([(list, 1)], 10, 0.9, 10);

        // Scores 11/11, 11/12, 11/13 -> only the top two clear 0.9.
        fused.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Fuse_AppliesLimit()
    {
        var list = new[] { Hit("a"), Hit("b"), Hit("c") };

        var fused = ReciprocalRankFusion.Fuse([(list, 1)], 60, 0, 2);

        fused.Select(r => r.Hash).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Fuse_PrefersTheFirstListsPayload_ForASharedHash()
    {
        var fts = new[] { new MemorySearchResult("a", 0, 0, "a.md", "keyword snippet") };
        var vec = new[] { new MemorySearchResult("a", 0, 0, "a.md", "vector teaser") };

        var fused = ReciprocalRankFusion.Fuse([(fts, 1), (vec, 1)], 60, 0, 10);

        var hit = fused.ShouldHaveSingleItem();
        hit.Snippet.ShouldBe("keyword snippet");
    }
}
