using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering.Policies;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class HermesProcessNoisePolicyTests
{
    private readonly HermesProcessNoisePolicy _sut = new();

    [Fact]
    public async Task EvaluateAsync_WithExactHermesTerminalSignature_ReturnsNoise()
    {
        // Arrange
        var content = @"[IMPORTANT: Background process proc_74af2deb49a7 completed normally (exit code 0).
Command: cd /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/fix-after-refactor && dotnet test
Output: ]";
        var request = new MemoryWriteRequest("proj-1", content);

        // Act
        var result = await _sut.EvaluateAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsNoise);
        Assert.Equal("HermesBackgroundProcessLog", result.PolicyName);
    }

    [Fact]
    public async Task EvaluateAsync_WithStandardText_ReturnsClean()
    {
        // Arrange
        var content = "This is just a normal memory note about a background process.";
        var request = new MemoryWriteRequest("proj-1", content);

        // Act
        var result = await _sut.EvaluateAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsNoise);
    }

    /// <summary>
    ///     A background process that FAILED is exactly as much machine noise as one that succeeded,
    ///     but the policy required the literal "completed normally". Measured read-only against the
    ///     live bank: of 29 "[IMPORTANT: Background process" items, only 18 carried that phrase --
    ///     11 failed-process notifications slipped through.
    /// </summary>
    [Theory]
    [InlineData("[IMPORTANT: Background process bash_7 failed (exit code 1).\nCommand: dotnet test\nOutput: x")]
    [InlineData("[IMPORTANT: Background process proc_2 was killed (exit code 137).\nCommand: npm run build")]
    public async Task EvaluateAsync_FailedBackgroundProcess_IsNoiseToo(string content)
    {
        var result = await new HermesProcessNoisePolicy()
            .EvaluateAsync(new MemoryWriteRequest("p", content), TestContext.Current.CancellationToken);

        result.IsNoise.ShouldBeTrue("a failed background process is machine output just as much as a successful one");
    }

    /// <summary>
    ///     The second notification shape this harness emits. Measured: 25 occurrences in the live
    ///     bank, none matched, all graded 2/5 when used as search queries.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_AsyncDelegationBatch_IsNoise()
    {
        const string content = "[ASYNC DELEGATION BATCH COMPLETE - 3 tasks finished]\nResults: ...";

        var result = await new HermesProcessNoisePolicy()
            .EvaluateAsync(new MemoryWriteRequest("p", content), TestContext.Current.CancellationToken);

        result.IsNoise.ShouldBeTrue();
    }

    /// <summary>Prose that merely mentions a background process is not noise.</summary>
    [Theory]
    [InlineData("The background process kept failing until we raised the timeout; see ADR-0031.")]
    [InlineData("Command: we agreed the release command should be documented in the runbook.")]
    public async Task EvaluateAsync_ProseAboutProcesses_StaysClean(string content)
    {
        var result = await new HermesProcessNoisePolicy()
            .EvaluateAsync(new MemoryWriteRequest("p", content), TestContext.Current.CancellationToken);

        result.IsNoise.ShouldBeFalse("widening the policy must not start eating memories that discuss processes");
    }

    /// <summary>
    ///     The third notification shape found in the live bank: process-signal notices. Measured: 7
    ///     occurrences in search_quality (six "received signal 1", one "received signal 15"), none
    ///     scored above grade 2.
    /// </summary>
    [Theory]
    [InlineData("received signal 1")]
    [InlineData("received signal 15")]
    [InlineData("received signal 15.")]
    public async Task EvaluateAsync_ReceivedSignal_IsNoise(string content)
    {
        var result = await new HermesProcessNoisePolicy()
            .EvaluateAsync(new MemoryWriteRequest("p", content), TestContext.Current.CancellationToken);

        result.IsNoise.ShouldBeTrue();
        result.PolicyName.ShouldBe("HermesBackgroundProcessLog");
    }

    /// <summary>Only the exact "received signal &lt;digits&gt;" shape is machine noise; surrounding text is prose.</summary>
    [Theory]
    [InlineData("we received signal 1 from the child process and had to investigate")]
    [InlineData("received signal 1 twice")]
    public async Task EvaluateAsync_ReceivedSignalWithSurroundingText_StaysClean(string content)
    {
        var result = await new HermesProcessNoisePolicy()
            .EvaluateAsync(new MemoryWriteRequest("p", content), TestContext.Current.CancellationToken);

        result.IsNoise.ShouldBeFalse("over-widening past the exact machine shape is the failure mode ADR-0039 warns about");
    }
}
