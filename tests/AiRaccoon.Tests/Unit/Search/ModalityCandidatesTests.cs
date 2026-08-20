using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;
using SearchResults = AiRaccoon.Infrastructure.Sqlite.Memory.SearchResults;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     Cross-context modality ordering (see docs/adr/0006-rrf-parameter-optimization.md): a
///     context contributing only one candidate must not automatically lead the merged list —
///     its absolute score decides its place among every other context's candidates.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModalityCandidatesTests
{
    [Fact]
    public void ByBm25_OrdersAcrossContexts_ByAbsoluteScore_NotPerContextRank()
    {
        // bm25 is negative; more negative is better. bigContext's own rank-1 (h1, -9.0) is the
        // true best match overall; smallContext's sole candidate (h4, -0.5) would be "rank 1 of
        // its own context" too, under the old per-context RRF.
        var bigContext = new[] { Hit("h1", -9.0), Hit("h2", -6.0), Hit("h3", -3.0) };
        var smallContext = new[] { Hit("h4", -0.5) };

        var ordered = ModalityCandidates.ByBm25(FtsResults(bigContext, smallContext));

        ordered.Select(r => r.Hash).ShouldBe(["h1", "h2", "h3", "h4"],
            "h4 is the weakest match by absolute bm25 despite being rank 1 of its own (tiny) context");
    }

    [Fact]
    public void ByCosine_OrdersAcrossContexts_ByAbsoluteScore_NotPerContextRank()
    {
        // Fused cosine score: higher is better. smallContext's sole candidate (h4, 0.1) is weak.
        var bigContext = new[] { Hit("h1", 0.95), Hit("h2", 0.8), Hit("h3", 0.5) };
        var smallContext = new[] { Hit("h4", 0.1) };

        var ordered = ModalityCandidates.ByCosine(VectorResults(bigContext, smallContext));

        ordered.Select(r => r.Hash).ShouldBe(["h1", "h2", "h3", "h4"],
            "h4 is the weakest match by absolute cosine score despite being rank 1 of its own (tiny) context");
    }

    [Fact]
    public void ByBm25_SingleContext_PreservesInputOrderExactly_ForBaselineParity()
    {
        // Stable-sort parity (see docs/adr/0006-rrf-parameter-optimization.md): a single context's
        // output must be bit-identical to its input order — SearchByFilter already orders
        // ascending — which is what keeps single-context (project-scope) baselines unchanged.
        var context = new[] { Hit("h1", -9.0), Hit("h2", -9.0), Hit("h3", -3.0) };

        ModalityCandidates.ByBm25(FtsResults(context)).Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
    }

    [Fact]
    public void ByCosine_SingleContext_PreservesInputOrderExactly_ForBaselineParity()
    {
        var context = new[] { Hit("h1", 0.9), Hit("h2", 0.9), Hit("h3", 0.2) };

        ModalityCandidates.ByCosine(VectorResults(context)).Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
    }

    [Fact]
    public void ByBm25_DedupesByHash_KeepingTheBestRanking()
    {
        var contextA = new[] { Hit("h1", -2.0) };
        var contextB = new[] { Hit("h1", -9.0) };

        var ordered = ModalityCandidates.ByBm25(FtsResults(contextA, contextB));

        ordered.ShouldHaveSingleItem().Ranking.ShouldBe(-9.0);
    }

    [Fact]
    public void ByCosine_DedupesByHash_KeepingTheBestRanking()
    {
        var contextA = new[] { Hit("h1", 0.3) };
        var contextB = new[] { Hit("h1", 0.9) };

        var ordered = ModalityCandidates.ByCosine(VectorResults(contextA, contextB));

        ordered.ShouldHaveSingleItem().Ranking.ShouldBe(0.9);
    }

    [Fact]
    public void ByCosine_DedupesByContent_PreferringTheProjectCopy_OverAPromotedSharedDuplicate()
    {
        // ADR-0035/WP3a: a promoted shared copy carries a different row hash than its project
        // original, so a hash-keyed group-by cannot see they are the same text — only the
        // content (valueByHash) can. The shared copy scores higher here on purpose: the
        // project copy must still win.
        var projectCopy = new MemorySearchResult("project-hash", 0.6, "abc123.md", "s");
        var sharedCopy = new MemorySearchResult("shared-hash", 0.9, "shared/def456.md", "s");
        var searchResults = VectorResults([projectCopy], [sharedCopy]);
        searchResults.Index.ValueByHash["project-hash"] = "identical promoted text";
        searchResults.Index.ValueByHash["shared-hash"] = "identical promoted text";

        var ordered = ModalityCandidates.ByCosine(searchResults);

        var hit = ordered.ShouldHaveSingleItem();
        hit.Hash.ShouldBe("project-hash");
    }

    [Fact]
    public void ByBm25_DedupesByContent_PreferringTheProjectCopy_OverAPromotedSharedDuplicate()
    {
        var projectCopy = new MemorySearchResult("project-hash", -2.0, "abc123.md", "s");
        var sharedCopy = new MemorySearchResult("shared-hash", -9.0, "shared/def456.md", "s");
        var searchResults = FtsResults([projectCopy], [sharedCopy]);
        searchResults.Index.ValueByHash["project-hash"] = "identical promoted text";
        searchResults.Index.ValueByHash["shared-hash"] = "identical promoted text";

        var ordered = ModalityCandidates.ByBm25(searchResults);

        var hit = ordered.ShouldHaveSingleItem();
        hit.Hash.ShouldBe("project-hash");
    }

    [Fact]
    public void ByCosine_WithValueByHash_DoesNotMergeDistinctContent()
    {
        var a = new MemorySearchResult("h1", 0.6, "a.md", "s");
        var b = new MemorySearchResult("h2", 0.9, "shared/h2.md", "s");
        var searchResults = VectorResults([a], [b]);
        searchResults.Index.ValueByHash["h1"] = "text a";
        searchResults.Index.ValueByHash["h2"] = "text b";

        var ordered = ModalityCandidates.ByCosine(searchResults);

        ordered.Select(r => r.Hash).ShouldBe(["h2", "h1"], "distinct content must never be merged");
    }

    private static MemorySearchResult Hit(string hash, double ranking) => new(hash, ranking, $"{hash}.md", "s");

    /// <summary>Builds the cross-context candidate collection with each context's results as an FTS batch.</summary>
    private static SearchResults FtsResults(params IReadOnlyList<MemorySearchResult>[] contexts)
    {
        var searchResults = new SearchResults();
        foreach (var context in contexts)
        {
            searchResults.AddResults(new VectorSearchResult([], TimeSpan.Zero), new FtsSearchResult(context, TimeSpan.Zero));
        }

        return searchResults;
    }

    /// <summary>Builds the cross-context candidate collection with each context's results as a vector batch.</summary>
    private static SearchResults VectorResults(params IReadOnlyList<MemorySearchResult>[] contexts)
    {
        var searchResults = new SearchResults();
        foreach (var context in contexts)
        {
            searchResults.AddResults(new VectorSearchResult(context, TimeSpan.Zero), new FtsSearchResult([], TimeSpan.Zero));
        }

        return searchResults;
    }
}
