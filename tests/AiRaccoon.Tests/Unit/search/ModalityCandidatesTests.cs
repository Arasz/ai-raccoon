using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

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

        var ordered = ModalityCandidates.ByBm25([bigContext, smallContext]);

        ordered.Select(r => r.Hash).ShouldBe(["h1", "h2", "h3", "h4"],
            "h4 is the weakest match by absolute bm25 despite being rank 1 of its own (tiny) context");
    }

    [Fact]
    public void ByCosine_OrdersAcrossContexts_ByAbsoluteScore_NotPerContextRank()
    {
        // Fused cosine score: higher is better. smallContext's sole candidate (h4, 0.1) is weak.
        var bigContext = new[] { Hit("h1", 0.95), Hit("h2", 0.8), Hit("h3", 0.5) };
        var smallContext = new[] { Hit("h4", 0.1) };

        var ordered = ModalityCandidates.ByCosine([bigContext, smallContext]);

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

        ModalityCandidates.ByBm25([context]).Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
    }

    [Fact]
    public void ByCosine_SingleContext_PreservesInputOrderExactly_ForBaselineParity()
    {
        var context = new[] { Hit("h1", 0.9), Hit("h2", 0.9), Hit("h3", 0.2) };

        ModalityCandidates.ByCosine([context]).Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
    }

    [Fact]
    public void ByBm25_DedupesByHash_KeepingTheBestRanking()
    {
        var contextA = new[] { Hit("h1", -2.0) };
        var contextB = new[] { Hit("h1", -9.0) };

        var ordered = ModalityCandidates.ByBm25([contextA, contextB]);

        ordered.ShouldHaveSingleItem().Ranking.ShouldBe(-9.0);
    }

    [Fact]
    public void ByCosine_DedupesByHash_KeepingTheBestRanking()
    {
        var contextA = new[] { Hit("h1", 0.3) };
        var contextB = new[] { Hit("h1", 0.9) };

        var ordered = ModalityCandidates.ByCosine([contextA, contextB]);

        ordered.ShouldHaveSingleItem().Ranking.ShouldBe(0.9);
    }

    private static MemorySearchResult Hit(string hash, double ranking) => new(hash, 0, ranking, $"{hash}.md", "s");
}
