using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class NoiseFeedbackCollectorTests
{
    [Fact]
    public void IsImmuneContent_WithAdrDocument_ReturnsTrue()
    {
        var content = "# ADR-0029: Pre-Write Noise Filtering Pipeline";
        NoiseFeedbackCollector.IsImmuneContent(content).ShouldBeTrue();
    }

    [Fact]
    public void IsImmuneContent_WithProcessLog_ReturnsFalse()
    {
        var content = "[IMPORTANT: Background process proc_123 completed normally...]";
        NoiseFeedbackCollector.IsImmuneContent(content).ShouldBeFalse();
    }

    [Fact]
    public void CheckOrthogonality_WhenSimilarityIsBelowThreshold_ReturnsTrue()
    {
        var noiseCentroid = new float[] { 1.0f, 0.0f, 0.0f };
        var coreCentroid = new float[] { 0.5f, 0.5f, 0.0f }; // similarity ~ 0.707 <= 0.75

        NoiseFeedbackCollector.CheckOrthogonality(noiseCentroid, coreCentroid).ShouldBeTrue();
    }

    [Fact]
    public void CheckOrthogonality_WhenSimilarityExceedsThreshold_ReturnsFalse()
    {
        var noiseCentroid = new float[] { 1.0f, 0.0f, 0.0f };
        var coreCentroid = new float[] { 0.95f, 0.05f, 0.0f }; // similarity ~ 0.95 > 0.75

        NoiseFeedbackCollector.CheckOrthogonality(noiseCentroid, coreCentroid).ShouldBeFalse();
    }
}
