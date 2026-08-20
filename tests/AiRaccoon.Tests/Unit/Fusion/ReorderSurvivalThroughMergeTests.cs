using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     What actually survives <see cref="SearchResultMerger.Merge" />, established rather than
///     assumed (docs/adr/0078). The reorder is the INPUT to source-affinity ranking, not the served
///     order: the second fusion carries it through untouched (ADR-0058), and
///     <see cref="SourceAffinityRanker" /> can then override it by roughly seven positions per
///     adjacent sibling at the shipped λ = 0.1, k = 60.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReorderSurvivalThroughMergeTests
{
    private const int RrfK = SearchQuery.DefaultRrfK;

    private const double ShippedLambda = 0.1;

    private static MemorySearchResult Chunk(string hash, string? source, int chunkIndex) => new(hash, 0, hash, "snippet", source, chunkIndex);

    /// <summary>With no adjacent siblings anywhere, λ adds nothing and the reorder reaches the caller intact.</summary>
    [Fact]
    public void Merge_OverAReorderedList_WithNoSiblings_ServesTheReorderedOrder()
    {
        var reordered = (IReadOnlyList<MemorySearchResult>)
            [.. Enumerable.Range(1, 6).Select(i => Chunk($"h{i}", $"f{i}.md", 0))];

        var served = SearchResultMerger.Merge(new SearchResult(reordered, TimeSpan.Zero), 10, 0.0, ShippedLambda);

        served.Select(r => r.Hash).ShouldBe(reordered.Select(r => r.Hash));
    }

    /// <summary>
    ///     The override, measured: a chunk the reorder placed 9th, flanked by two siblings from the
    ///     same file, outranks the chunk the reorder placed 1st. 61/69 + 0.1×2 = 1.0841 beats 1.0.
    ///     Anything the reorder decides is therefore a proposal source affinity may still revise.
    /// </summary>
    [Fact]
    public void Merge_AdjacentChunkBoost_CanOverrideTheReorderedTopResult()
    {
        var reordered = (IReadOnlyList<MemorySearchResult>)
        [
            Chunk("lone", "lone.md", 0),
            .. Enumerable.Range(2, 6).Select(i => Chunk($"filler{i}", $"f{i}.md", 0)),
            Chunk("cluster-a", "cluster.md", 3),
            Chunk("cluster-b", "cluster.md", 4),
            Chunk("cluster-c", "cluster.md", 5)
        ];

        var served = SearchResultMerger.Merge(new SearchResult(reordered, TimeSpan.Zero), 10, 0.0, ShippedLambda);

        served[0].Hash.ShouldBe("cluster-b");
        served[1].Hash.ShouldBe("lone");
    }

    /// <summary>λ = 0 — the path a source-path query takes — leaves the reorder as the served order.</summary>
    [Fact]
    public void Merge_WithSourceAffinityOff_ServesTheReorderedOrderEvenWithSiblingsPresent()
    {
        var reordered = (IReadOnlyList<MemorySearchResult>)
        [
            Chunk("lone", "lone.md", 0),
            Chunk("cluster-a", "cluster.md", 3),
            Chunk("cluster-b", "cluster.md", 4),
            Chunk("cluster-c", "cluster.md", 5)
        ];

        var served = SearchResultMerger.Merge(new SearchResult(reordered, TimeSpan.Zero), 10, 0.0, sourceLambda: 0.0);

        served.Select(r => r.Hash).ShouldBe(reordered.Select(r => r.Hash));
    }

    /// <summary>
    ///     The reorder feeds a strict total order in, and the second fusion turns position into a
    ///     distinct score — so no tie reaches Merge's ThenBy(Path) tie-break to decide the top hit
    ///     by filename.
    /// </summary>
    [Fact]
    public void Merge_OverAReorderedList_ServesDistinctRankings()
    {
        var fts = ModalityLeg.From("fts", [Chunk("target", null, 0), Chunk("c1", null, 0), Chunk("c2", null, 0)]);
        var vector = ModalityLeg.From("vector", [Chunk("c1", null, 0), Chunk("c2", null, 0)]);
        var fused = (IReadOnlyList<MemorySearchResult>)
            [Chunk("c1", null, 0), Chunk("c2", null, 0), Chunk("target", null, 0)];

        var served = SearchResultMerger.Merge(
            new SearchResult(NoFusionRegression.Reorder(fused, [fts, vector]), TimeSpan.Zero), 10, 0.0, ShippedLambda);

        served.Select(r => r.Hash).ShouldBe(["c1", "target", "c2"]);
        served.Select(r => r.Ranking).ShouldBeUnique();
    }
}
