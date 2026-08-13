using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait("Speed", "Fast")]
public class ZeroShotEmbeddingFilterTests
{
    [Fact]
    public void IsNoise_WhenDistanceIsBelowThreshold_ReturnsTrue()
    {
        float[] doc = { 1.0f, 0.0f, 0.0f };
        float[] noise = { 0.9f, 0.1f, 0.0f };

        ZeroShotEmbeddingFilter.IsNoise(doc, noise).ShouldBeTrue();
    }

    [Fact]
    public void IsNoise_WhenDistanceIsAboveThreshold_ReturnsFalse()
    {
        float[] doc = { 1.0f, 0.0f, 0.0f };
        float[] noise = { 0.0f, 1.0f, 0.0f };

        ZeroShotEmbeddingFilter.IsNoise(doc, noise).ShouldBeFalse();
    }
}
