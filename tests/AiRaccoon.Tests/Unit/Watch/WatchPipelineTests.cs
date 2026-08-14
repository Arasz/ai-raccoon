using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Single channel, per-path pending aggregation, 1s tick, drain to scheduler, containment.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchPipelineTests
{
    private const string Project = "acme";

    [Fact]
    public async Task Tick_BurstOfEventsForOnePath_ProducesOneDigestOfFinalContent()
    {
        using var dir = TempDir.New("pipeline-burst");
        var file = dir.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await File.WriteAllTextAsync(file, "v2", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await File.WriteAllTextAsync(file, "v3", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.Memory.Ingested[0].Content.ShouldBe("v3");
    }

    [Fact]
    public async Task Tick_MixedCreateChangeRenameBurst_CollapsesIntoOneTick()
    {
        using var dir = TempDir.New("pipeline-mixed");
        var oldFile = dir.File("old.md");
        var newFile = dir.File("new.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        await File.WriteAllTextAsync(oldFile, "final", TestContext.Current.CancellationToken);

        stack.Pipeline.Enqueue(new WatchEvent(Project, oldFile, WatchEventKind.Created));
        stack.Pipeline.Enqueue(new WatchEvent(Project, oldFile, WatchEventKind.Changed));
        File.Move(oldFile, newFile);
        stack.Pipeline.Enqueue(new WatchEvent(Project, newFile, WatchEventKind.Renamed, oldFile));

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        // One tick: the old path resolves to a delete (file gone), the new path is ingested once.
        stack.Memory.DeletedPaths.ShouldContain((Project, oldFile));
        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.Memory.Ingested[0].Path.ShouldBe(newFile);
    }

    [Fact]
    public async Task Tick_EventsForMultipleFiles_EachDigestedOnce()
    {
        using var dir = TempDir.New("pipeline-multi");
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        var c = dir.File("c.md");
        await File.WriteAllTextAsync(a, "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(b, "b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(c, "c", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);

        stack.Pipeline.Enqueue(new WatchEvent(Project, a, WatchEventKind.Created));
        stack.Pipeline.Enqueue(new WatchEvent(Project, a, WatchEventKind.Changed));
        stack.Pipeline.Enqueue(new WatchEvent(Project, b, WatchEventKind.Created));
        stack.Pipeline.Enqueue(new WatchEvent(Project, c, WatchEventKind.Created));

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Tick_FileModifiedDuringItsOwnDigest_IsPickedUpByTheNextTick()
    {
        using var dir = TempDir.New("pipeline-inflight");
        var file = dir.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Memory.OnIngest = _ => hold.Task;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        var firstTick = stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        await stack.Memory.FirstIngestTcs.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The digest is in flight when the file changes again.
        await File.WriteAllTextAsync(file, "v2", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        hold.SetResult();
        await firstTick;

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Count.ShouldBe(2);
        stack.Memory.Ingested[1].Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Tick_FileDeletedWhileDigestInFlight_ResolvesToDeleted_NoResurrect()
    {
        using var dir = TempDir.New("pipeline-delete-inflight");
        var file = dir.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Memory.OnIngest = _ => hold.Task;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        var firstTick = stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        await stack.Memory.FirstIngestTcs.Task.WaitAsync(TestContext.Current.CancellationToken);

        File.Delete(file);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Deleted));
        hold.SetResult();
        await firstTick;

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        stack.Memory.Ingested.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tick_FileDeletedBeforeDigestRuns_NoIngestNoResurrect()
    {
        using var dir = TempDir.New("pipeline-delete-before");
        var file = dir.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        File.Delete(file);

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        stack.Memory.DeletedPaths.ShouldContain((Project, file));
    }

    [Fact]
    public async Task Tick_PendingDigestForRemovedWatch_DoesNotResurrectTheWatch()
    {
        using var dir = TempDir.New("pipeline-removed");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        stack.Pipeline.UnregisterWatch(Project, dir.Path);
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        stack.Pipeline.GetStatuses(Project).ShouldBeEmpty();
    }

    /// <summary>
    ///     D21 gap 3: a watch unregistered while its digest job is already in flight (dispatched from
    ///     the current tick's drain, running inside <c>RunJobAsync</c>, outside the tick's lock).
    ///     <c>UnregisterWatch</c> removes the `_runtime` entry immediately; <c>RunJobAsync</c>'s
    ///     completion path must not resurrect it — a watch the caller already removed must not
    ///     reappear in <c>GetStatuses</c> just because a job dispatched before the removal is still
    ///     finishing.
    /// </summary>
    [Fact]
    public async Task Tick_UnregisteredWhileDigestInFlight_RuntimeStatusStaysGone()
    {
        using var dir = TempDir.New("pipeline-unregister-inflight");
        var file = dir.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Memory.OnIngest = _ => hold.Task;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        var tick = stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        // The digest is in flight (job already dispatched out of _pending, running RunJobAsync)
        // when the watch is unregistered — UnregisterWatch does not, and cannot, reach into a job
        // the scheduler already owns.
        await stack.Memory.FirstIngestTcs.Task.WaitAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem().State.ShouldBe(WatchState.Scanning);
        stack.Pipeline.UnregisterWatch(Project, dir.Path);
        stack.Pipeline.GetStatuses(Project).ShouldBeEmpty("unregister must drop the runtime entry immediately");

        hold.SetResult();
        await tick;

        stack.Pipeline.GetStatuses(Project).ShouldBeEmpty(
            "an unregistered watch must not reappear just because its in-flight job finishes afterward");
    }

    /// <summary>
    ///     D21 gap 3, the register side: a brand-new watch registered while a tick for a different,
    ///     already-registered watch has an in-flight digest. <c>RegisterWatch</c> and the tick's
    ///     pending-drain both take the same <c>_gate</c> lock, so this is mutual exclusion, not a
    ///     torn read — the new watch must land with a Scanning status and the in-flight watch's own
    ///     digest must be unaffected by the concurrent registration.
    /// </summary>
    [Fact]
    public async Task Tick_NewWatchRegisteredWhileAnotherWatchsDigestIsInFlight_BothEndUpCorrect()
    {
        using var dirA = TempDir.New("pipeline-register-inflight-a");
        using var dirB = TempDir.New("pipeline-register-inflight-b");
        var fileA = dirA.File("a.md");
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dirA.Path);
        await File.WriteAllTextAsync(fileA, "v1", TestContext.Current.CancellationToken);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Memory.OnIngest = _ => hold.Task;
        stack.Pipeline.Enqueue(new WatchEvent(Project, fileA, WatchEventKind.Created));

        var tick = stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        await stack.Memory.FirstIngestTcs.Task.WaitAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.RegisterWatch(Project, dirB.Path);
        stack.Pipeline.GetStatuses(Project).Single(s => s.Path == dirB.Path).State.ShouldBe(WatchState.Scanning);

        hold.SetResult();
        await tick;

        var statuses = stack.Pipeline.GetStatuses(Project);
        statuses.Count.ShouldBe(2);
        statuses.Single(s => s.Path == dirA.Path).State.ShouldBe(WatchState.Healthy);
        statuses.Single(s => s.Path == dirB.Path).State.ShouldBe(WatchState.Scanning);
        stack.Memory.Ingested.ShouldHaveSingleItem("the concurrent registration must not disturb the in-flight digest for the other watch");
    }

    [Fact]
    public async Task Tick_EventOutsideAnyRegisteredWatch_IsDropped()
    {
        using var dir = TempDir.New("pipeline-outside");
        using var otherDir = TempDir.New("pipeline-outside-other");
        var file = otherDir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_RegisterWatch_StartsScanning_FirstSuccessFlipsHealthy()
    {
        using var dir = TempDir.New("pipeline-status-healthy");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);

        stack.Pipeline.GetStatuses(Project).Single().State.ShouldBe(WatchState.Scanning);

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var status = stack.Pipeline.GetStatuses(Project).Single();
        status.State.ShouldBe(WatchState.Healthy);
        status.LastError.ShouldBeNull();
        status.LastSync.ShouldBe(WatchTestStack.FixedNow);
    }

    [Fact]
    public async Task Status_DigestFailure_SetsRetryingWithError_NextSuccessClearsIt()
    {
        using var dir = TempDir.New("pipeline-status-retry");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Memory.IngestError = new IOException("disk boom");
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var failed = stack.Pipeline.GetStatuses(Project).Single();
        failed.State.ShouldBe(WatchState.Retrying);
        failed.LastError.ShouldNotBeNull().ShouldContain("disk boom");

        stack.Memory.IngestError = null;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        stack.Time.Advance(WatchRetryPolicy.BackoffFor(1));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var healed = stack.Pipeline.GetStatuses(Project).Single();
        healed.State.ShouldBe(WatchState.Healthy);
        healed.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Status_FiveConsecutiveFailures_StopsChecking_RegistrationAndStatusKept()
    {
        using var dir = TempDir.New("pipeline-status-stopped");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Memory.IngestError = new IOException("always fails");

        for (var failure = 1; failure <= 4; failure++)
        {
            stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
            await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
            stack.Pipeline.GetStatuses(Project).Single().State.ShouldBe(WatchState.Retrying);
            stack.Time.Advance(WatchRetryPolicy.BackoffFor(failure));
        }

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var stopped = stack.Pipeline.GetStatuses(Project).Single();
        stopped.State.ShouldBe(WatchState.Stopped);
        stopped.LastError.ShouldNotBeNull();

        // A stopped watch keeps its registration but no longer digests anything.
        var attemptsBefore = stack.Memory.Ingested.Count;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Count.ShouldBe(attemptsBefore);
        stack.Pipeline.GetStatuses(Project).Single().State.ShouldBe(WatchState.Stopped);
    }

    [Fact]
    public async Task Status_BackoffGatesRetries_UntilBackoffElapses()
    {
        using var dir = TempDir.New("pipeline-backoff");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Memory.IngestError = new IOException("boom");
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        // A tick before the backoff elapses must not attempt a digest. DeletedPaths.Count
        // stands in for "an attempt ran": the executor deletes-then-reingests on every attempt, regardless of the injected failure.
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        stack.Time.Advance(TimeSpan.FromMilliseconds(500));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Count.ShouldBe(0);
        stack.Memory.DeletedPaths.Count.ShouldBe(1);

        stack.Time.Advance(TimeSpan.FromMilliseconds(500));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.DeletedPaths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Status_RegisterWatchAgain_DoesNotResetRuntimeState()
    {
        using var dir = TempDir.New("pipeline-readd");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Pipeline.GetStatuses(Project).Single().State.ShouldBe(WatchState.Healthy);

        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Pipeline.GetStatuses(Project).Single().State.ShouldBe(WatchState.Healthy);
    }

    [Fact]
    public async Task Loop_RunAsync_TicksOnOneSecondCadence_AndSurvivesFailures()
    {
        using var dir = TempDir.New("pipeline-loop");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = stack.Pipeline.RunAsync(cts.Token);

        // First tick (~1s): the initial create is ingested.
        await AdvanceUntilAsync(stack, () => stack.Memory.FirstIngestTcs.Task.IsCompleted, cts.Token);
        stack.Memory.Ingested.ShouldHaveSingleItem();

        // A digest failure must not kill the loop: the next tick still runs and succeeds.
        // (Content changes so the digest is not hash-skipped and reaches the failing ingest.)
        stack.Memory.IngestError = new IOException("boom");
        await File.WriteAllTextAsync(file, "v2", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await AdvanceUntilAsync(stack,
            () => stack.Pipeline.GetStatuses(Project).Single().State == WatchState.Retrying, cts.Token);

        stack.Memory.IngestError = null;
        await File.WriteAllTextAsync(file, "v3", TestContext.Current.CancellationToken);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await AdvanceUntilAsync(stack,
            () => stack.Pipeline.GetStatuses(Project).Single().State == WatchState.Healthy, cts.Token);

        await cts.CancelAsync();
        await run;
    }

    /// <summary>Drives the fake clock forward in small steps until the condition holds (loop cadence is real-async).</summary>
    private static async Task AdvanceUntilAsync(WatchTestStack stack, Func<bool> condition,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met while advancing the fake clock.");
            }

            stack.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10, cancellationToken);
        }
    }
}
