using System.Diagnostics;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Observability;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using AiRaccoon.Tests.Unit.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Watch;

/// <summary>
///     Re-watch loop semantics: disabled projects keep registrations without checking; enabled
///     registrations get a watcher + catch-up scan (full when never synced, since-watermark
///     otherwise); removed/disabled flips stop the watcher; StopAsync disposes everything.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchHostedServiceTests
{
    private const string Project = "acme";

    private static (WatchTestStack Stack, WatchEventSource Source, WatchCatchUp CatchUp, WatchHostedService Hosted)
        NewStack(IOperationTelemetry? telemetry = null)
    {
        var stack = new WatchTestStack();
        var source = new WatchEventSource(stack.Pipeline.Enqueue, _ => { },
            NullLogger<WatchEventSource>.Instance);
        var catchUp = new WatchCatchUp(stack.Pipeline, stack.Store, stack.ScanGuard, stack.ScanLease, stack.Time,
            NullLogger<WatchCatchUp>.Instance, stack.IgnoreRules);
        var hosted = new WatchHostedService(stack.Memory, stack.Store, stack.Pipeline, source, catchUp, stack.Time,
            telemetry ?? TestTelemetry.None, NullLogger<WatchHostedService>.Instance);
        return (stack, source, catchUp, hosted);
    }

    [Fact]
    public async Task Reconcile_NoOpPass_EmitsNoSpan_ButRecordsTheDurationAndCount()
    {
        // Nothing registered, so nothing starts/stops/unregisters: a poll-loop tick that happens
        // ~86,400 times a day and is almost always this. Counted, never spanned.
        using var probe = new BackgroundTelemetryProbe(WatchHostedService.OperationName);
        var (_, _, _, hosted) = NewStack(probe.Telemetry);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        probe.Spans.ShouldBeEmpty();
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public async Task Reconcile_WhenAWatcherStarts_EmitsASpan()
    {
        using var dir = TempDir.New("hosted-span-on-work");
        using var probe = new BackgroundTelemetryProbe(WatchHostedService.OperationName);
        var (stack, _, _, hosted) = NewStack(probe.Telemetry);
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_WhenThePassThrows_RecordsTheFailure()
    {
        using var probe = new BackgroundTelemetryProbe(WatchHostedService.OperationName);
        var (stack, _, _, hosted) = NewStack(probe.Telemetry);
        stack.Store.ListWatchesError = new InvalidOperationException("zephyrone");

        await Should.ThrowAsync<InvalidOperationException>(
            () => hosted.ReconcileAsync(TestContext.Current.CancellationToken));

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Reconcile_DisabledProject_KeepsRegistration_AndStartsNoWatcher()
    {
        using var dir = TempDir.New("hosted-disabled");
        var (stack, source, _, hosted) = NewStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        source.IsWatching(Project, dir.Path).ShouldBeFalse();
        stack.Store.Watches.ShouldContainKey((Project, dir.Path));
        var status = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        status.Path.ShouldBe(dir.Path);
        status.State.ShouldBe(WatchState.Scanning);
    }

    [Fact]
    public async Task Reconcile_EnabledNeverSynced_StartsWatcher_AndRunsAFullInitialScan()
    {
        using var dir = TempDir.New("hosted-full");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "zephyrone", TestContext.Current.CancellationToken);
        var (stack, source, catchUp, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        source.IsWatching(Project, dir.Path).ShouldBeTrue();
        var scan = catchUp.LastScan.ShouldNotBeNull();
        await scan;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(file);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_EnabledWithWatermark_RunsASinceScan_OnlyNewerFilesQueued()
    {
        using var dir = TempDir.New("hosted-since");
        var (stack, source, catchUp, hosted) = NewStack();
        var watermark = stack.Time.GetUtcNow().ToUnixTimeSeconds();
        var older = dir.File("older.md");
        var newer = dir.File("newer.md");
        await File.WriteAllTextAsync(older, "zephyrone", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(newer, "zephyrtwo", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(older, DateTimeOffset.FromUnixTimeSeconds(watermark - 3600).UtcDateTime);
        File.SetLastWriteTimeUtc(newer, DateTimeOffset.FromUnixTimeSeconds(watermark + 3600).UtcDateTime);
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, watermark,
            TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, IngestPath.Normalize(older), "zephyrhash",
            watermark - 3600, TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        source.IsWatching(Project, dir.Path).ShouldBeTrue();
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(newer);
        stack.Memory.Ingested.Select(i => i.Path).ShouldNotContain(older);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_RemovedRegistration_StopsTheWatcher()
    {
        using var dir = TempDir.New("hosted-removed");
        var (stack, source, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        source.IsWatching(Project, dir.Path).ShouldBeTrue();

        await stack.Store.RemoveWatchAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        source.IsWatching(Project, dir.Path).ShouldBeFalse();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_DisableFlip_StopsTheWatcher_ButKeepsTheRegistration()
    {
        using var dir = TempDir.New("hosted-flip");
        var (stack, source, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        source.IsWatching(Project, dir.Path).ShouldBeTrue();

        stack.Memory.Settings[WatchConfigKeys.EnabledProject(Project)] = "false";
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        source.IsWatching(Project, dir.Path).ShouldBeFalse();
        stack.Store.Watches.ShouldContainKey((Project, dir.Path));
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_StaleRegistration_UnregistersFromThePipeline()
    {
        using var dir = TempDir.New("hosted-stale-unregister");
        var (stack, _, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.GetStatuses(Project).ShouldBeEmpty();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_StaleRegistration_StopsResolvingItsFilesForDigest()
    {
        using var dir = TempDir.New("hosted-stale-digest");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "zephyrone", TestContext.Current.CancellationToken);
        var (stack, _, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_StaleRegistrationOfADisabledProject_UnregistersFromThePipeline()
    {
        using var dir = TempDir.New("hosted-stale-disabled");
        var (stack, _, _, hosted) = NewStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        await stack.Store.RemoveWatchAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.GetStatuses(Project).ShouldBeEmpty();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconcile_DisableFlip_KeepsThePipelineRegistration()
    {
        using var dir = TempDir.New("hosted-flip-keeps-registration");
        var (stack, _, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.Memory.Settings[WatchConfigKeys.EnabledProject(Project)] = "false";
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_DisposesAllWatchers()
    {
        using var dir = TempDir.New("hosted-stop");
        var (stack, source, _, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        source.IsWatching(Project, dir.Path).ShouldBeTrue();

        await hosted.StopAsync(CancellationToken.None);

        source.IsWatching(Project, dir.Path).ShouldBeFalse();
    }

    /// <summary>
    ///     A remove and a re-add that both land between two reconcile polls must still start a
    ///     fresh scan — the store shows the registration present at the next poll either way, so
    ///     <c>_active</c> alone cannot tell "still running" from "removed, then re-added".
    /// </summary>
    [Fact]
    public async Task Reconcile_RemoveThenReAddBetweenPolls_StartsANewScan()
    {
        using var dir = TempDir.New("hosted-remove-readd");
        var (stack, source, catchUp, hosted) = NewStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        var firstScan = catchUp.LastScan.ShouldNotBeNull();
        await firstScan;

        // Both the remove and the re-add complete before the hosted service polls again, so
        // ListWatchesAsync shows the registration present at the next reconcile either way.
        await stack.Service.RemoveAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.ScanGuard.StartedScans.ShouldBe(2, "the re-add must start a second scan, not join/skip the first");
        catchUp.LastScan.ShouldNotBeSameAs(firstScan);
        source.IsWatching(Project, dir.Path).ShouldBeTrue();
        await hosted.StopAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Host shutdown must cancel a scan that is still walking the tree, not let it run to
    ///     completion after the poll loop has already exited.
    /// </summary>
    [Fact]
    public async Task StopAsync_CancelsAnInFlightScan()
    {
        using var dir = TempDir.New("hosted-stop-cancels-scan");
        await File.WriteAllTextAsync(dir.File("a.md"), "zephyrone", TestContext.Current.CancellationToken);
        var (stack, _, catchUp, hosted) = NewStack();
        stack.Enable();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        var insideScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Store.OnListFiles = async () =>
        {
            insideScan.TrySetResult();
            await holdScan.Task;
        };

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        await insideScan.Task.WaitAsync(TestContext.Current.CancellationToken);

        await hosted.StopAsync(CancellationToken.None);
        holdScan.SetResult();
        await catchUp.LastScan!;

        stack.Store.ListFilesTokens[0].IsCancellationRequested.ShouldBeTrue("host shutdown must cancel an in-flight scan");
    }

    [Fact]
    public async Task ExecuteAsync_ReconcilesOnEachPoll_AndPicksUpNewRegistrations()
    {
        using var dir = TempDir.New("hosted-loop");
        var (stack, source, catchUp, hosted) = NewStack();
        stack.Enable();
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "zephyrone", TestContext.Current.CancellationToken);
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0,
            TestContext.Current.CancellationToken);

        _ = hosted.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Step budget, not a clock (PR #464): the poll loop runs on fake time.
            for (var step = 0; step < 6_000 && catchUp.LastScan is null; step++)
            {
                stack.Time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            var scan = catchUp.LastScan.ShouldNotBeNull("the poll loop did not pick up the registration");
            source.IsWatching(Project, dir.Path).ShouldBeTrue();
            await scan;
            await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
            stack.Memory.Ingested.Select(i => i.Path).ShouldContain(file);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Without the once-per-process <c>_active</c> gate, a directory whose watermark never
    ///     advances would full-scan once per second, forever (docs/plans/2026-08-07-watch-scan-runaway-fix.md).
    /// </summary>
    [Fact]
    public async Task Reconcile_CalledTwice_EnqueuesOnlyOneScan()
    {
        using var dir = TempDir.New("hosted-once");
        var (stack, _, catchUp, hosted) = NewStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await File.WriteAllTextAsync(dir.File("a.md"), "zephyrone", TestContext.Current.CancellationToken);
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        // Let the first scan finish: the scan guard only single-flights an in-flight scan, so a
        // second scan here could only have been stopped by the _active gate.
        await catchUp.LastScan!;
        await hosted.ReconcileAsync(TestContext.Current.CancellationToken);

        stack.ScanGuard.StartedScans.ShouldBe(1);
        stack.ScanGuard.SkippedScans.ShouldBe(0);
    }
}
