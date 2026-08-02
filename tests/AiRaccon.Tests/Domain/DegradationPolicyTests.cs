using AiRaccon.Core.Degradation;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Domain;

public class DegradationPolicyTests
{
    [Fact]
    public void ShouldDegrade_OldAndLowRated_ReturnsTrue()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.1, ageDays: 40, threshold: 0.5, ttlDays: 30)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldDegrade_YoungEntry_ReturnsFalse()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.1, ageDays: 10, threshold: 0.5, ttlDays: 30)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldDegrade_HighlyRatedEntry_ReturnsFalse()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.8, ageDays: 40, threshold: 0.5, ttlDays: 30)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldDegrade_WhenRatingEqualsThreshold_ReturnsFalse()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.5, ageDays: 40, threshold: 0.5, ttlDays: 30)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldDegrade_WhenAgeEqualsTtl_ReturnsFalse()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.1, ageDays: 30, threshold: 0.5, ttlDays: 30)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldDegrade_WithPerEntryTtlOverride_UsesOverride()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.1, ageDays: 10, threshold: 0.5, ttlDays: 30, ttlOverrideDays: 5)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldDegrade_WithPerEntryTtlOverrideGreaterThanAge_ReturnsFalse()
    {
        DegradationPolicy.ShouldDegrade(rating: 0.1, ageDays: 10, threshold: 0.5, ttlDays: 30, ttlOverrideDays: 20)
            .ShouldBeFalse();
    }
}
