using System;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class HermesProcessNoisePolicyTests
{
    private readonly HermesProcessNoisePolicy _sut = new();

    [Fact]
    public void Evaluate_WithExactHermesTerminalSignature_ReturnsNoise()
    {
        // Arrange
        var content = @"[IMPORTANT: Background process proc_74af2deb49a7 completed normally (exit code 0).
Command: cd /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/fix-after-refactor && dotnet test
Output: ]";
        var request = new MemoryWriteRequest("proj-1", content, null, null, null, null, null);

        // Act
        var result = _sut.Evaluate(request);

        // Assert
        Assert.True(result.IsNoise);
        Assert.Equal("HermesBackgroundProcessLog", result.PolicyName);
    }

    [Fact]
    public void Evaluate_WithStandardText_ReturnsClean()
    {
        // Arrange
        var content = "This is just a normal memory note about a background process.";
        var request = new MemoryWriteRequest("proj-1", content, null, null, null, null, null);

        // Act
        var result = _sut.Evaluate(request);

        // Assert
        Assert.False(result.IsNoise);
    }
}
