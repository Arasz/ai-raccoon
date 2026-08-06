using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionCapacityPolicyTests
{
    // ------------------------------------------------------------------ reservation
    [Theory]
    [InlineData(1000, 5, 200)]
    [InlineData(1000, 1, 1000)]
    [InlineData(1000, 8, 125)]
    [InlineData(10, 3, 3)]
    public void ReservationFor_DividesCapByProjectCount(int cap, int projects, int expected)
    {
        PromotionCapacityPolicy.ReservationFor(cap, projects).ShouldBe(expected);
    }

    [Theory]
    [InlineData(5, 10)]   // more projects than slots: no guaranteed space
    [InlineData(1000, 0)] // no projects: nothing to reserve for
    [InlineData(0, 5)]    // zero cap: nothing to reserve
    public void ReservationFor_ReturnsZero_WhenNothingToGuarantee(int cap, int projects)
    {
        PromotionCapacityPolicy.ReservationFor(cap, projects).ShouldBe(0);
    }

    // ------------------------------------------------------------------ eviction trigger
    [Theory]
    [InlineData(1001, 1000, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(999, 1000, false)]
    [InlineData(1, 0, true)]
    public void NeedsEviction_TriggersOnlyAboveCap(int total, int cap, bool expected)
    {
        PromotionCapacityPolicy.NeedsEviction(total, cap).ShouldBe(expected);
    }

    // ------------------------------------------------------------------ eviction target
    [Fact]
    public void EvictionTarget_PicksTheGreatestCountProject()
    {
        var policy = new UniformCountEvictionPolicy();
        var target = policy.EvictionTarget(new Dictionary<string, int>
        {
            ["acme"] = 2,
            ["other"] = 7,
            ["third"] = 4
        });

        target.ShouldBe("other");
    }

    [Fact]
    public void EvictionTarget_TieBreaksByOrdinalSmallestId()
    {
        var policy = new UniformCountEvictionPolicy();
        var target = policy.EvictionTarget(new Dictionary<string, int>
        {
            ["zeta"] = 5,
            ["alpha"] = 5,
            ["mid"] = 3
        });

        target.ShouldBe("alpha", "ordinal string order breaks the count tie");
    }

    [Fact]
    public void EvictionTarget_EmptyQueue_ReturnsNull()
    {
        new UniformCountEvictionPolicy().EvictionTarget(new Dictionary<string, int>()).ShouldBeNull();
    }

    // ------------------------------------------------------------------ capacity info
    [Fact]
    public void CapacityInfo_FlagsBorrowingAndReservations()
    {
        var info = PromotionCapacityPolicy.CapacityInfo(1000, 5,
            new Dictionary<string, int> { ["acme"] = 250, ["other"] = 100, ["third"] = 0 });

        info.Count.ShouldBe(3);
        var acme = info["acme"];
        acme.Reserved.ShouldBe(200);
        acme.Used.ShouldBe(250);
        acme.Borrowing.ShouldBeTrue();

        var other = info["other"];
        other.Reserved.ShouldBe(200);
        other.Used.ShouldBe(100);
        other.Borrowing.ShouldBeFalse();

        info["third"].Borrowing.ShouldBeFalse();
    }

    [Fact]
    public void CapacityInfo_EmptyCounts_YieldsEmptyInfo()
    {
        PromotionCapacityPolicy.CapacityInfo(1000, 5, new Dictionary<string, int>())
            .ShouldBeEmpty();
    }
}
