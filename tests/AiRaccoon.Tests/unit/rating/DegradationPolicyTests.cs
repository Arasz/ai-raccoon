using AiRaccoon.Core.Degradation;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class DegradationPolicyTests
{
    [Fact]
    public void ShouldDegrade_OldAndLowRated_ReturnsTrue() =>
        DegradationPolicy.ShouldDegrade(0.1, 40, 0.5, 30)
            .ShouldBeTrue();

    [Fact]
    public void ShouldDegrade_YoungEntry_ReturnsFalse() =>
        DegradationPolicy.ShouldDegrade(0.1, 10, 0.5, 30)
            .ShouldBeFalse();

    [Fact]
    public void ShouldDegrade_HighlyRatedEntry_ReturnsFalse() =>
        DegradationPolicy.ShouldDegrade(0.8, 40, 0.5, 30)
            .ShouldBeFalse();

    [Fact]
    public void ShouldDegrade_WhenRatingEqualsThreshold_ReturnsFalse() =>
        DegradationPolicy.ShouldDegrade(0.5, 40, 0.5, 30)
            .ShouldBeFalse();

    [Fact]
    public void ShouldDegrade_WhenAgeEqualsTtl_ReturnsFalse() =>
        DegradationPolicy.ShouldDegrade(0.1, 30, 0.5, 30)
            .ShouldBeFalse();

    [Fact]
    public void ShouldDegrade_WithPerEntryTtlOverride_UsesOverride() =>
        DegradationPolicy.ShouldDegrade(0.1, 10, 0.5, 30, 5)
            .ShouldBeTrue();

    [Fact]
    public void ShouldDegrade_WithPerEntryTtlOverrideGreaterThanAge_ReturnsFalse() =>
        DegradationPolicy.ShouldDegrade(0.1, 10, 0.5, 30, 20)
            .ShouldBeFalse();
}
