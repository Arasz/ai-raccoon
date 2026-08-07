using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     Re-watch loop semantics: disabled projects keep registrations without checking; enabled
///     registrations get a watcher + catch-up scan (full when never synced, since-watermark
///     otherwise); removed/disabled flips stop the watcher; StopAsync disposes everything.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchHostedServiceTests
{
    private const string Project = "acme";

    private static (WatchTestStack Stack, WatchEventSource Source, WatchCatchUp CatchUp, WatchHostedService Hosted)
        NewStack()
    {
        var stack = new WatchTestStack();
        var source = new WatchEventSource(stack.Pipeline.Enqueue, _ => { },
            NullLogger<WatchEventSource>.Instance);
        var catchUp = new WatchCatchUp(stack.Pipeline, stack.Store, NullLogger<WatchCatchUp>.Instance);
        var hosted = new WatchHostedService(stack.Memory, stack.Store, stack.Pipeline, source, catchUp, stack.Time,
            NullLogger<WatchHostedService>.Instance);
        return (stack, source, catchUp, hosted);
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
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (catchUp.LastScan is null && DateTime.UtcNow < deadline)
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
}
