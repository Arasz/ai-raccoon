using System.Diagnostics;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.TestHelpers;

/// <summary>
///     Pins the runner's lifetime contract: a run that outlasts its hard cap leaves nothing behind,
///     a dead process is not an error to kill, and a missing build output names the path it wanted.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RaccoonProcessTests
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Process.Dispose() does not kill, so a `using var process` that times out orphans the child
    ///     and every grandchild it spawned — measured against the real binary before this existed.
    /// </summary>
    [Fact]
    public async Task ARunThatOutlastsItsHardCap_LeavesNoProcessTreeBehind()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tree is built with a POSIX shell");
        var marker = Path.Combine(TestData.CreateTempRoot("kill-tree"), "grandchild.pid");

        var run = RaccoonProcess.RunAsync("/bin/sh",
            ["-c", $"sh -c 'echo $$ > \"{marker}\"; exec sleep 120' & wait"],
            HardCap, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<TimeoutException>(async () => await run);

        var grandchild = int.Parse((await File.ReadAllTextAsync(marker,
            TestContext.Current.CancellationToken)).Trim());
        (await HasRetiredAsync(grandchild)).ShouldBeTrue(
            $"pid {grandchild} outlived the run that spawned it");
    }

    /// <summary>The stderr of a killed run is the only clue left about why it hung.</summary>
    [Fact]
    public async Task ARunThatOutlastsItsHardCap_ReportsWhatItKilled()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tree is built with a POSIX shell");

        var thrown = await Should.ThrowAsync<TimeoutException>(async () =>
            await RaccoonProcess.RunAsync("/bin/sh", ["-c", "echo boom 1>&2; sleep 120"],
                HardCap, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("/bin/sh");
        thrown.Message.ShouldContain("boom");
    }

    /// <summary>A teardown running after a failed setup holds a process that was never started.</summary>
    [Fact]
    public void KillingAProcessThatWasNeverStarted_IsNotAnError()
    {
        using var process = new Process();

        // Both HasExited and Kill throw InvalidOperationException here.
        Should.NotThrow(() => RaccoonProcess.KillTree(process));
    }

    [Fact]
    public void AMissingBuildOutput_NamesThePathItExpected()
    {
        // Without this the failure is an opaque Win32Exception raised from inside the MCP SDK.
        var empty = TestData.CreateTempRoot("no-build-output");

        var thrown = Should.Throw<FileNotFoundException>(() => RaccoonProcess.ResolveExecutable(empty));

        thrown.Message.ShouldContain(empty);
    }

    private static async Task<bool> HasRetiredAsync(int pid) =>
        await WaitByPolling.WaitForAsync(pid, static candidate =>
        {
            try
            {
                using var process = Process.GetProcessById(candidate);
                return ValueTask.FromResult(process.HasExited);
            }
            catch (ArgumentException)
            {
                return ValueTask.FromResult(true);
            }
        }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, Settle, TimeProvider.System,
            TestContext.Current.CancellationToken);
}
