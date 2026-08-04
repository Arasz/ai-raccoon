using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Retrieval.Metrics;

public sealed class NdcgTests
{
    private static readonly IReadOnlySet<string> Relevant = new HashSet<string>(["A", "C"], StringComparer.Ordinal);

    [Fact]
    public void NdcgAtK_HandComputedExample_RanksHitFirst()
    {
        // DCG = 1/log2(2) + 0 = 1; IDCG = 1/log2(2) + 1/log2(3); nDCG = 0.6131471927654584
        RetrievalMetrics.NdcgAtK(["A", "B", "C", "D"], Relevant, k: 2).ShouldBe(0.6131471927654584, 1e-9);
    }

    [Fact]
    public void NdcgAtK_PerfectRanking_IsOne()
    {
        RetrievalMetrics.NdcgAtK(["A", "C"], Relevant, k: 10).ShouldBe(1.0);
    }

    [Fact]
    public void NdcgAtK_RelevantRankedThird_ScoredBelowPerfect()
    {
        // Only A at rank 3 counts: DCG = 1/log2(4); IDCG over two relevant docs.
        RetrievalMetrics.NdcgAtK(["X", "Y", "A", "B"], Relevant, k: 3).ShouldBe(0.3065735963827292, 1e-9);
    }

    [Fact]
    public void NdcgAtK_NoRelevantHits_IsZero()
    {
        RetrievalMetrics.NdcgAtK(["X", "Y", "Z"], new HashSet<string>(["Q"]), k: 5).ShouldBe(0.0);
    }

    [Fact]
    public void NdcgAtK_EmptyRelevance_IsZero()
    {
        RetrievalMetrics.NdcgAtK(["A", "B"], new HashSet<string>(), k: 5).ShouldBe(0.0);
    }

    [Fact]
    public void NdcgAtK_ZeroOrNegativeK_IsZero()
    {
        RetrievalMetrics.NdcgAtK(["A", "B"], Relevant, k: 0).ShouldBe(0.0);
    }
}

public sealed class MrrTests
{
    [Fact]
    public void Mrr_FirstRelevantAtRankTwo_IsOneHalf()
    {
        RetrievalMetrics.Mrr(["X", "A", "B"], new HashSet<string>(["A"])).ShouldBe(0.5);
    }

    [Fact]
    public void Mrr_FirstRelevantAtRankOne_IsOne()
    {
        RetrievalMetrics.Mrr(["A", "B"], new HashSet<string>(["A"])).ShouldBe(1.0);
    }

    [Fact]
    public void Mrr_NoRelevantHit_IsZero()
    {
        RetrievalMetrics.Mrr(["X", "Y"], new HashSet<string>(["A"])).ShouldBe(0.0);
    }
}

public sealed class RecallAtKTests
{
    private static readonly IReadOnlySet<string> Relevant = new HashSet<string>(["A", "C", "E"], StringComparer.Ordinal);

    [Fact]
    public void RecallAtK_OneOfThreeRelevantWithinTopTwo_IsOneThird()
    {
        // Top 2 = [A, B]; only A is relevant -> 1/3.
        RetrievalMetrics.RecallAtK(["A", "B", "C"], Relevant, k: 2).ShouldBe(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void RecallAtK_KBeyondListLength_CountsAllHits()
    {
        RetrievalMetrics.RecallAtK(["A", "B", "C"], Relevant, k: 5).ShouldBe(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void RecallAtK_ZeroK_IsZero()
    {
        RetrievalMetrics.RecallAtK(["A", "B"], Relevant, k: 0).ShouldBe(0.0);
    }

    [Fact]
    public void RecallAtK_EmptyRelevance_IsZero()
    {
        RetrievalMetrics.RecallAtK(["A", "B"], new HashSet<string>(), k: 5).ShouldBe(0.0);
    }
}

public sealed class KendallTauTests
{
    [Fact]
    public void KendallTau_IdenticalLists_IsOne()
    {
        RetrievalMetrics.KendallTau(["A", "B", "C", "D"], ["A", "B", "C", "D"]).ShouldBe(1.0);
    }

    [Fact]
    public void KendallTau_ReversedLists_IsMinusOne()
    {
        RetrievalMetrics.KendallTau(["A", "B", "C", "D"], ["D", "C", "B", "A"]).ShouldBe(-1.0);
    }

    [Fact]
    public void KendallTau_SingleAdjacentSwap_IsTwoThirds()
    {
        // 5 concordant pairs, 1 discordant: (5 - 1) / 6.
        RetrievalMetrics.KendallTau(["A", "B", "C", "D"], ["A", "B", "D", "C"]).ShouldBe(2.0 / 3.0, 1e-9);
    }

    [Fact]
    public void KendallTau_PartialOverlap_ComparesOnlyCommonItems()
    {
        RetrievalMetrics.KendallTau(["A", "B", "C"], ["B", "C", "D"]).ShouldBe(1.0);
    }

    [Fact]
    public void KendallTau_NoCommonItems_IsZero()
    {
        RetrievalMetrics.KendallTau(["A", "B"], ["C", "D"]).ShouldBe(0.0);
    }

    [Fact]
    public void KendallTau_SingleCommonItem_HasNoPairsToCompare_IsZero()
    {
        RetrievalMetrics.KendallTau(["A"], ["A"]).ShouldBe(0.0);
    }
}
