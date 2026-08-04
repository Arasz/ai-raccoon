using AiRaccoon.Core.Rating;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Rating;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class RatingPolicyTests
{
    [Fact]
    public void Rating_AfterOneHalfLifeWithNoAccesses_HalvesBaseScore() =>
        RatingPolicy.Rating(0.5, 0, 30, 30)
            .ShouldBe(0.25, 1e-9);

    [Fact]
    public void Rating_NeverSearchedAndVeryOld_ApproachesZero() =>
        RatingPolicy.Rating(0.5, 0, 1000, 30)
            .ShouldBeLessThan(0.001);

    [Fact]
    public void Rating_EachAccess_RaisesRating()
    {
        var withoutAccess = RatingPolicy.Rating(0.5, 0, 0, 30);
        var withAccess = RatingPolicy.Rating(0.5, 1, 0, 30);

        withAccess.ShouldBeGreaterThan(withoutAccess);
    }

    [Fact]
    public void Rating_SingleAccessAtAgeZero_AppliesAccessMultiplier() =>
        RatingPolicy.Rating(0.5, 1, 0, 30)
            .ShouldBe(0.55, 1e-9);

    [Fact]
    public void Rating_RecentHighAccess_BeatsOldLowAccess()
    {
        var recent = RatingPolicy.Rating(0.5, 10, 0, 30);
        var old = RatingPolicy.Rating(0.5, 0, 90, 30);

        recent.ShouldBeGreaterThan(old);
    }

    [Fact]
    public void Rating_WithNonPositiveHalfLife_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(0.5, 0, 10, 0));

    [Fact]
    public void Rating_WithNegativeAccessCount_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(0.5, -1, 10, 30));

    [Fact]
    public void Rating_WithNegativeAge_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(0.5, 0, -1, 30));
}
