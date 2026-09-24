using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>Which fused row the source-affinity boost may not overtake.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FusionLeaderTests
{
    [Fact]
    public void Of_BothLegsRankTheSameRowFirst_IsThatRow()
    {
        var leader = FusionLeader.Of([Hit("a"), Hit("b")], fts: [Hit("a")], vector: [Hit("a"), Hit("b")], allTermsMatched: Set());

        leader.ShouldBe("a");
    }

    [Fact]
    public void Of_KeywordTopMatchingEveryTermAndWinningFusion_IsThatRow()
    {
        var leader = FusionLeader.Of([Hit("id"), Hit("c0"), Hit("c1")], fts: [Hit("id")], vector: [Hit("c0"), Hit("c1"), Hit("id")],
            allTermsMatched: Set("id"));

        leader.ShouldBe("id");
    }

    [Fact]
    public void Of_KeywordTopMatchingOnlySomeTerms_IsNoLeader()
    {
        var leader = FusionLeader.Of([Hit("id"), Hit("c0")], fts: [Hit("id")], vector: [Hit("c0"), Hit("id")],
            allTermsMatched: Set("other"));

        leader.ShouldBeNull();
    }

    [Fact]
    public void Of_FusedTopIsNotTheKeywordTop_IsNoLeader()
    {
        var leader = FusionLeader.Of([Hit("c0"), Hit("id")], fts: [Hit("id")], vector: [Hit("c0"), Hit("id")],
            allTermsMatched: Set("id"));

        leader.ShouldBeNull();
    }

    [Fact]
    public void Of_EmptyLegs_IsNoLeader()
    {
        FusionLeader.Of([], fts: [], vector: [], allTermsMatched: Set()).ShouldBeNull();
    }

    private static HashSet<string> Set(params string[] hashes) => new(hashes, StringComparer.Ordinal);

    private static MemorySearchResult Hit(string hash) => new(hash, 1.0, hash, "s", null, 0, 5);
}
