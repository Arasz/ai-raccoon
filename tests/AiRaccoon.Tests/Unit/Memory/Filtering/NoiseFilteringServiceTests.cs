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

    [Fact]
    public async Task EvaluatePreWriteAsync_WhenTheFirstPolicyMatches_NeverEvaluatesTheSecond()
    {
        // First-match short-circuit (QA-F3): the loop in EvaluatePreWriteAsync must return on the
        // first IsNoise result without consulting the remaining policies.
        var second = new CountingNoisePolicy("would-also-match");
        var policies = new INoiseFilterPolicy[] { new HermesProcessNoisePolicy(), second };
        var sut = new NoiseFilteringService(policies);

        var content = @"[IMPORTANT: Background process proc_qa completed normally (exit code 0).
Command: cd /tmp && echo test
Output: test]";
        var request = new MemoryWriteRequest("proj-1", content);

        var result = await sut.EvaluatePreWriteAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsNoise);
        Assert.Equal("HermesBackgroundProcessLog", result.PolicyName);
        Assert.Equal(0, second.EvaluationCount);
    }

    /// <summary>Always reports noise, and counts how many times it was asked — the short-circuit test's
    /// secondary observable: the return value alone cannot prove the second policy was skipped.</summary>
    private sealed class CountingNoisePolicy(string name) : INoiseFilterPolicy
    {
        public int EvaluationCount { get; private set; }

        public string Name => name;

        public ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            EvaluationCount++;
            return ValueTask.FromResult(NoiseFilterResult.Noise(name));
        }
    }
}
