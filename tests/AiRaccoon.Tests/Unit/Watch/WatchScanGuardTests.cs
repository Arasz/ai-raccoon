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

    /// <summary>
    ///     D21 gap 2: <see cref="WatchScanGuard.StartedScans" />/<see cref="WatchScanGuard.SkippedScans" />
    ///     are plain <c>int</c> auto-properties with no <c>volatile</c>/<c>Interlocked</c> of their own —
    ///     they are safe only because every increment happens inside <c>lock (_gate)</c>. This hammers
    ///     that claim: many distinct keys (forcing StartedScans) and many racers per key (forcing
    ///     SkippedScans), all released from one <see cref="Barrier" /> so the lock is genuinely
    ///     contended, not just theoretically shared. If the increments ever moved outside the lock,
    ///     concurrent writers would lose updates and the totals below would come up short.
    /// </summary>
    [Fact]
    public async Task Run_HighContentionAcrossManyKeysAndRacers_CountersReconcileExactly()
    {
        const int keyCount = 10;
        const int racersPerKey = 3;
        const int totalRacers = keyCount * racersPerKey;
        var dirs = Enumerable.Range(0, keyCount).Select(i => TempDir.New($"scanguard-hammer-{i}")).ToArray();
        try
        {
            var guard = new WatchScanGuard();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scansStarted = 0;

            Task Scan(CancellationToken ct)
            {
                Interlocked.Increment(ref scansStarted);
                return gate.Task.WaitAsync(ct);
            }

            using var start = new Barrier(totalRacers);
            var joined = new Task[totalRacers];
            var threads = new Thread[totalRacers];
            var slot = 0;
            foreach (var dir in dirs)
            {
                var path = dir.Path;
                for (var r = 0; r < racersPerKey; r++)
                {
                    var mySlot = slot++;
                    threads[mySlot] = new Thread(() =>
                    {
                        start.SignalAndWait(Patience);
                        joined[mySlot] = guard.Run(Project, path, Scan, TestContext.Current.CancellationToken);
                    });
                    threads[mySlot].Start();
                }
            }

            foreach (var thread in threads)
            {
                thread.Join(Patience).ShouldBeTrue("every racer thread must finish dispatching Run within the patience window");
            }

            gate.SetResult();
            await Task.WhenAll(joined).WaitAsync(Patience, TestContext.Current.CancellationToken);

            guard.StartedScans.ShouldBe(keyCount, "exactly one starter per distinct key");
            guard.SkippedScans.ShouldBe(totalRacers - keyCount, "every non-starting racer for a contended key must be counted as skipped");
            (guard.StartedScans + guard.SkippedScans).ShouldBe(totalRacers, "no increment may be lost under contention");
            scansStarted.ShouldBe(keyCount, "the scan body itself must run exactly once per key, regardless of how many racers joined it");
        }
        finally
        {
            foreach (var dir in dirs)
            {
                dir.Dispose();
            }
        }
    }
}
