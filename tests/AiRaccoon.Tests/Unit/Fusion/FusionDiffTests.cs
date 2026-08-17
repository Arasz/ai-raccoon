using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     The evidence the flag-enabled path collects (docs/adr/0078): the offline corpus cannot
///     adjudicate a fusion change (ADR-0072), so the enabled path records how the served list
///     differed from the baseline one, joined to `search_quality` by correlation id.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FusionDiffTests
{
    private static IReadOnlyList<MemorySearchResult> Hits(params string[] hashes) =>
        [.. hashes.Select(hash => new MemorySearchResult(hash, 0, hash, "snippet"))];

    [Fact]
    public void Between_IdenticalLists_RecordsNoChange()
    {
        var diff = FusionDiff.Between(Hits("a", "b", "c"), Hits("a", "b", "c"));

        diff.Top1Changed.ShouldBe(0);
        diff.Top1RankDelta.ShouldBe(0);
        diff.Top5Moved.ShouldBe(0);
    }

    [Fact]
    public void Between_TopResultReplaced_RecordsHowFarTheOldWinnerFell()
    {
        var diff = FusionDiff.Between(Hits("a", "b", "c"), Hits("c", "a", "b"));

        diff.Top1Changed.ShouldBe(1);
        diff.Top1RankDelta.ShouldBe(1);
        diff.Top5Moved.ShouldBe(3);
    }

    /// <summary>A reorder can push the baseline winner past the limit; that is not a delta of zero.</summary>
    [Fact]
    public void Between_BaselineWinnerLeavesTheServedList_RecordsTheDroppedSentinel()
    {
        var diff = FusionDiff.Between(Hits("a", "b"), Hits("b", "c"));

        diff.Top1Changed.ShouldBe(1);
        diff.Top1RankDelta.ShouldBe(FusionDiff.Dropped);
    }

    /// <summary>Movement below the winner is a different, cheaper risk than changing the answer.</summary>
    [Fact]
    public void Between_TopHeldButTailReshuffled_SeparatesBreadthFromTheAnswerChanging()
    {
        var diff = FusionDiff.Between(Hits("a", "b", "c", "d"), Hits("a", "c", "b", "d"));

        diff.Top1Changed.ShouldBe(0);
        diff.Top1RankDelta.ShouldBe(0);
        diff.Top5Moved.ShouldBe(2);
    }

    [Fact]
    public void Between_EmptyLists_RecordsNoChange()
    {
        var diff = FusionDiff.Between([], []);

        diff.Top1Changed.ShouldBe(0);
        diff.Top5Moved.ShouldBe(0);
    }

    /// <summary>
    ///     Mirrors SearchTimings.PhaseNames: the names are declared, not reflected, so a fourth
    ///     signal means editing MetricNames, Measurements() and this test together.
    /// </summary>
    [Fact]
    public void MetricNames_OneEntryPerRecordedSignal_PrefixedWithSearchFusion()
    {
        FusionDiff.MetricNames.ShouldBe(
        [
            "search.fusion.top1_changed",
            "search.fusion.top1_rank_delta",
            "search.fusion.top5_moved"
        ]);
    }

    [Fact]
    public void Measurements_EmitOneRowPerMetricName_InDeclarationOrder()
    {
        var diff = FusionDiff.Between(Hits("a", "b", "c"), Hits("c", "a", "b"));

        diff.Measurements().Select(m => m.Name).ShouldBe(FusionDiff.MetricNames);
        diff.Measurements().Select(m => m.Value).ShouldBe([1, 1, 3]);
        diff.Measurements().Select(m => m.Unit).ShouldBe(["flag", "ranks", "results"]);
    }

    /// <summary>The flag is off unless the setting explicitly says so; an absent value keeps the default.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void ParseNoRegressionEnabled_DefaultsOff(string? value, bool expected)
    {
        FusionConfigKeys.ParseNoRegressionEnabled(value).ShouldBe(expected);
        FusionConfigKeys.DefaultNoRegressionEnabled.ShouldBeFalse();
        FusionConfigKeys.NoRegressionEnabledGlobal.ShouldBe("fusion.noRegression.enabled.global");
    }
}
