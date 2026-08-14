using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     Single-flight: concurrent Run calls for the same (projectId, path) join the in-flight scan;
///     different watches scan independently; Cancel stops only the watch it names.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchScanGuardTests
{
    private const string Project = "acme";

    /// <summary>Deadline for every wait in the racing test, so a lost race fails instead of hanging.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Run_SameWatchTwiceWhileTheFirstIsRunning_StartsOneScan()
    {
        using var dir = TempDir.New("scanguard-single-flight");
        var guard = new WatchScanGuard();
        var store = new FakeWatchStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Scan(CancellationToken ct)
        {
            await store.ListFilesAsync(Project, ct);
            entered.TrySetResult();
            await gate.Task;
        }

        var first = guard.Run(Project, dir.Path, Scan, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var second = guard.Run(Project, dir.Path, Scan, TestContext.Current.CancellationToken);
        gate.SetResult();
        await first;

        guard.StartedScans.ShouldBe(1);
        guard.SkippedScans.ShouldBe(1);
        store.ListFilesCalls.ShouldBe(1);
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task Run_RacedForTheSameWatch_StartsOneScanAndJoinsIt()
    {
        using var dir = TempDir.New("scanguard-race");
        var guard = new WatchScanGuard();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var start = new Barrier(2);
        var scans = 0;

        async Task Scan(CancellationToken ct)
        {
            Interlocked.Increment(ref scans);
            await gate.Task.WaitAsync(ct);
        }

        var joined = new Task[2];
        var racers = new Task[2];
        for (var i = 0; i < racers.Length; i++)
        {
            var slot = i;
            racers[slot] = Task.Run(() =>
            {
                start.SignalAndWait(Patience);
                joined[slot] = guard.Run(Project, dir.Path, Scan, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(racers).WaitAsync(Patience, TestContext.Current.CancellationToken);
        gate.SetResult();
        await Task.WhenAll(joined).WaitAsync(Patience, TestContext.Current.CancellationToken);

        guard.StartedScans.ShouldBe(1);
        guard.SkippedScans.ShouldBe(1);
        scans.ShouldBe(1);
        ReferenceEquals(joined[0], joined[1]).ShouldBeTrue();
    }

    [Fact]
    public async Task Run_DifferentWatches_StartBothScans()
    {
        using var dirA = TempDir.New("scanguard-multi-a");
        using var dirB = TempDir.New("scanguard-multi-b");
        var guard = new WatchScanGuard();

        var first = guard.Run(Project, dirA.Path, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        var second = guard.Run(Project, dirB.Path, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await first;
        await second;

        guard.StartedScans.ShouldBe(2);
        guard.SkippedScans.ShouldBe(0);
    }

    [Fact]
    public async Task Run_AfterThePreviousScanCompleted_StartsAgain()
    {
        using var dir = TempDir.New("scanguard-sequential");
        var guard = new WatchScanGuard();

        await guard.Run(Project, dir.Path, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await guard.Run(Project, dir.Path, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        guard.StartedScans.ShouldBe(2);
        guard.SkippedScans.ShouldBe(0);
    }

    [Fact]
    public async Task Run_AfterAFailedScan_StartsAgain()
    {
        using var dir = TempDir.New("scanguard-failed");
        var guard = new WatchScanGuard();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            guard.Run(Project, dir.Path, _ => throw new InvalidOperationException("boom"),
                TestContext.Current.CancellationToken));
        await guard.Run(Project, dir.Path, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        guard.StartedScans.ShouldBe(2);
    }

    [Fact]
    public async Task Cancel_WithAScanInFlight_CancelsOnlyThatWatch()
    {
        using var dirA = TempDir.New("scanguard-cancel-a");
        using var dirB = TempDir.New("scanguard-cancel-b");
        var guard = new WatchScanGuard();
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenA = default(CancellationToken);
        var tokenB = default(CancellationToken);

        var scanA = guard.Run(Project, dirA.Path, async ct =>
        {
            tokenA = ct;
            enteredA.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, TestContext.Current.CancellationToken);
        var scanB = guard.Run(Project, dirB.Path, async ct =>
        {
            tokenB = ct;
            enteredB.TrySetResult();
            await gateB.Task.WaitAsync(ct);
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(enteredA.Task, enteredB.Task).WaitAsync(TestContext.Current.CancellationToken);

        guard.Cancel(Project, dirA.Path);
        await Should.ThrowAsync<OperationCanceledException>(() => scanA);

        tokenA.IsCancellationRequested.ShouldBeTrue();
        tokenB.IsCancellationRequested.ShouldBeFalse();

        gateB.SetResult();
        await scanB;
    }
}
