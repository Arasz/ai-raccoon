using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionCapacityPolicyTests
{
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

    [Theory]
    [InlineData(1001, 1000, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(999, 1000, false)]
    [InlineData(1, 0, true)]
    public void NeedsEviction_TriggersOnlyAboveCap(int total, int cap, bool expected)
    {
        PromotionCapacityPolicy.NeedsEviction(total, cap).ShouldBe(expected);
    }

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

    [Fact]
    public void CapacityFor_FlagsBorrowing_WhenUsedExceedsTheReservation()
    {
        PromotionCapacityPolicy.CapacityFor(1000, 5, 250)
            .ShouldBe(new PromotionCapacityInfo(Reserved: 200, Used: 250, Borrowing: true));
    }

    [Theory]
    [InlineData(100, 200, false)]
    [InlineData(200, 200, false)]
    public void CapacityFor_UpToTheReservation_IsNotBorrowing(int used, int reserved, bool borrowing)
    {
        PromotionCapacityPolicy.CapacityFor(1000, 5, used)
            .ShouldBe(new PromotionCapacityInfo(reserved, used, borrowing));
    }

    [Fact]
    public void CapacityFor_MoreProjectsThanSlots_ReservesNothing()
    {
        PromotionCapacityPolicy.CapacityFor(3, 5, 1)
            .ShouldBe(new PromotionCapacityInfo(Reserved: 0, Used: 1, Borrowing: true));
    }
}
