using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class NoiseFilteringServiceTests
{
    [Fact]
    public async Task EvaluatePreWriteAsync_WhenPolicyMatches_ReturnsNoiseNamingThePolicy()
    {
        // Arrange
        var policies = new INoiseFilterPolicy[] { new HermesProcessNoisePolicy() };
        var sut = new NoiseFilteringService(policies);

        var content = @"[IMPORTANT: Background process proc_qa completed normally (exit code 0).
Command: cd /tmp && echo test
Output: test]";
        var request = new MemoryWriteRequest("proj-1", content);

        // Act
        var result = await sut.EvaluatePreWriteAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsNoise);
        Assert.Equal("HermesBackgroundProcessLog", result.PolicyName);
    }

    [Fact]
    public async Task EvaluatePreWriteAsync_WhenNoPolicyMatches_ReturnsClean()
    {
        var policies = new INoiseFilterPolicy[] { new HermesProcessNoisePolicy() };
        var sut = new NoiseFilteringService(policies);
        var request = new MemoryWriteRequest("proj-1", "an ordinary architectural note about the write path");

        var result = await sut.EvaluatePreWriteAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsNoise);
    }

    [Fact]
    public async Task EvaluatePreWriteAsync_WithNoPolicies_ReturnsClean()
    {
        var sut = new NoiseFilteringService([]);
        var request = new MemoryWriteRequest("proj-1", "anything at all");

        var result = await sut.EvaluatePreWriteAsync(request, TestContext.Current.CancellationToken);

        Assert.False(result.IsNoise);
    }
}
