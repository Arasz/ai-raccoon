using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     Source-affinity scoring (see docs/plans/retrieval-improvement-c.md §3 Wave 3):
///     adjacent-chunk boost, source consolidation and document-first tie-breaking over the
///     fused candidate list.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SourceAffinityRankerTests
{
    [Fact]
    public void Rank_ZeroLambda_ReturnsInputUnchanged()
    {
        var candidates = new[]
        {
            Hit("h1", 0.9, "a.md", 0),
            Hit("h2", 1.0, "a.md", 1)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.0, double.PositiveInfinity, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["h1", "h2"]);
        ranked[0].Ranking.ShouldBe(0.9);
        ranked[1].Ranking.ShouldBe(1.0);
    }

    [Fact]
    public void Rank_AdjacentSiblings_ReceiveLambdaPerSibling()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.9, "a.md", 1),
            Hit("h2", 0.8, "a.md", 2)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, double.PositiveInfinity, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["h0", "h1", "h2"]);
        ranked[0].Ranking.ShouldBe(1.0); // 1.1 / 1.1
        ranked[1].Ranking.ShouldBe(1.0); // 1.1 / 1.1 (path tie-break)
        ranked[2].Ranking.ShouldBe(0.9 / 1.1, 1e-9);
    }

    [Fact]
    public void Rank_NonAdjacentChunks_AreNotBoosted()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h2", 0.9, "a.md", 2)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, double.PositiveInfinity, DocScoreFormula.Max);

        ranked[0].Ranking.ShouldBe(1.0);
        ranked[1].Ranking.ShouldBe(0.9);
    }

    [Fact]
    public void Rank_SiblingBelowVisibilityThreshold_IsNotCounted()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.85, "a.md", 1)
        };

        var wide = SourceAffinityRanker.Rank(candidates, lambda: 0.1, 0.2, DocScoreFormula.Max);
        wide[0].Ranking.ShouldBe(1.0); // (1.0 + 0.1) / 1.1
        wide[1].Ranking.ShouldBe(0.95 / 1.1, 1e-9);

        var narrow = SourceAffinityRanker.Rank(candidates, lambda: 0.1, 0.1, DocScoreFormula.Max);
        narrow[0].Ranking.ShouldBe(1.0); // no boost
        narrow[1].Ranking.ShouldBe(0.95); // 0.85 + 0.1 (h0 still visible to h1)
    }

    [Fact]
    public void Rank_DocumentScoreTieBreak_SortsStrongerSourceFirst()
    {
        var candidates = new[]
        {
            Hit("a0", 1.0, "a.md", 0),
            Hit("a1", 0.4, "a.md", 1),
            Hit("b0", 0.5, "b.md", 0)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, double.PositiveInfinity, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["a0", "a1", "b0"]);
    }

    [Fact]
    public void Rank_Consolidation_MergesWeakAdjacentSiblingIntoBestChunk()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.7, "a.md", 1),
            Hit("h2", 0.9, "a.md", 2)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, 0.1, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["h0", "h2"]);
    }

    [Fact]
    public void Rank_Consolidation_KeepsStrongAdjacentSibling()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.95, "a.md", 1)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, 0.1, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["h0", "h1"]);
        ranked[0].Ranking.ShouldBe(1.0); // 1.1 / 1.1
        ranked[1].Ranking.ShouldBe(1.05 / 1.1, 1e-9);
    }

    [Fact]
    public void Rank_EmptyCandidates_ReturnsEmpty()
    {
        var ranked = SourceAffinityRanker.Rank([], lambda: 0.1, 0.1, DocScoreFormula.Max);

        ranked.ShouldBeEmpty();
    }

    [Fact]
    public void Rank_ChunksWithoutSource_AreSingletons()
    {
        var candidates = new[]
        {
            new MemorySearchResult("h1", 1, 1.0, "p1", "s"),
            new MemorySearchResult("h2", 2, 0.9, "p2", "s")
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, 0.1, DocScoreFormula.Max);

        ranked.Select(r => r.Hash).ShouldBe(["h1", "h2"]);
        ranked[0].Ranking.ShouldBe(1.0);
        ranked[1].Ranking.ShouldBe(0.9);
    }

    [Fact]
    public void Rank_BoostedScores_NormalizeToMax()
    {
        var candidates = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.9, "a.md", 1)
        };

        var ranked = SourceAffinityRanker.Rank(candidates, lambda: 0.1, double.PositiveInfinity, DocScoreFormula.Max);

        ranked[0].Ranking.ShouldBe(1.0); // top stays 1.0 (ranking contract)
    }

    [Fact]
    public void Merge_AppliesSourceAffinity_WhenLambdaIsSet()
    {
        var batch = new[]
        {
            Hit("h0", 1.0, "a.md", 0),
            Hit("h1", 0.9, "a.md", 1),
            Hit("h2", 0.8, "b.md", 0)
        };

        var merged = SearchResultMerger.Merge([batch], 10, sourceLambda: 0.1,
            consolidationThreshold: double.PositiveInfinity, formula: DocScoreFormula.Max);

        merged.Select(r => r.Hash).ShouldBe(["h0", "h1", "h2"]);
    }

    private static MemorySearchResult Hit(string hash, double ranking, string sourceFile, int chunkIndex) => new(hash, 0, ranking, $"{sourceFile}#{chunkIndex}", "s", sourceFile, chunkIndex, 5);
}
