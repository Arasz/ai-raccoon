using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class NoiseFilteringServiceTests
{
    [Fact]
    public async Task EvaluatePreWriteAsync_WhenPolicyMatches_ReturnsTrueAndRecordsNoise()
    {
        // Arrange
        var fakeStore = new FakeNoiseStore();
        var policies = new INoiseFilterPolicy[] { new HermesProcessNoisePolicy() };
        var sut = new NoiseFilteringService(policies, fakeStore, TimeProvider.System);

        var content = @"[IMPORTANT: Background process proc_qa completed normally (exit code 0).
Command: cd /tmp && echo test
Output: test]";
        var request = new MemoryWriteRequest("proj-1", content);

        // Act
        var result = await sut.EvaluatePreWriteAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.True(fakeStore.RecordCalled);
        Assert.Equal("HermesBackgroundProcessLog", fakeStore.RecordedPolicyName);
    }

    private class FakeNoiseStore : INoiseStore
    {
        public bool RecordCalled { get; private set; }
        public string? RecordedPolicyName { get; private set; }

        public Task RecordNoiseAsync(MemoryWriteRequest request, string policyName, int expiresAtUnixSeconds, CancellationToken cancellationToken = default)
        {
            RecordCalled = true;
            RecordedPolicyName = policyName;
            return Task.CompletedTask;
        }
    }
}
