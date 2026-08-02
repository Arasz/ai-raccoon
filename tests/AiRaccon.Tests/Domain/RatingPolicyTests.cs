using AiRaccon.Core.Rating;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Domain;

public class RatingPolicyTests
{
    [Fact]
    public void Rating_AfterOneHalfLifeWithNoAccesses_HalvesBaseScore()
    {
        RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: 30, halfLifeDays: 30)
            .ShouldBe(0.25, 1e-9);
    }

    [Fact]
    public void Rating_NeverSearchedAndVeryOld_ApproachesZero()
    {
        RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: 1000, halfLifeDays: 30)
            .ShouldBeLessThan(0.001);
    }

    [Fact]
    public void Rating_EachAccess_RaisesRating()
    {
        var withoutAccess = RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: 0, halfLifeDays: 30);
        var withAccess = RatingPolicy.Rating(baseScore: 0.5, accessCount: 1, ageDays: 0, halfLifeDays: 30);

        withAccess.ShouldBeGreaterThan(withoutAccess);
    }

    [Fact]
    public void Rating_SingleAccessAtAgeZero_AppliesAccessMultiplier()
    {
        RatingPolicy.Rating(baseScore: 0.5, accessCount: 1, ageDays: 0, halfLifeDays: 30)
            .ShouldBe(0.55, 1e-9);
    }

    [Fact]
    public void Rating_RecentHighAccess_BeatsOldLowAccess()
    {
        var recent = RatingPolicy.Rating(baseScore: 0.5, accessCount: 10, ageDays: 0, halfLifeDays: 30);
        var old = RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: 90, halfLifeDays: 30);

        recent.ShouldBeGreaterThan(old);
    }

    [Fact]
    public void Rating_WithNonPositiveHalfLife_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: 10, halfLifeDays: 0));
    }

    [Fact]
    public void Rating_WithNegativeAccessCount_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(baseScore: 0.5, accessCount: -1, ageDays: 10, halfLifeDays: 30));
    }

    [Fact]
    public void Rating_WithNegativeAge_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            RatingPolicy.Rating(baseScore: 0.5, accessCount: 0, ageDays: -1, halfLifeDays: 30));
    }
}
