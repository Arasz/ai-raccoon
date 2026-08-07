using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using static System.IO.File;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     D1 catch-up: never-synced watches (watermark 0) full-scan; otherwise only files with
///     mtime strictly after the watermark are queued. Scans are async — enqueue returns before
///     the digest work happens (feature rule 4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchCatchUpTests
{
    private const string Project = "acme";

    private static WatchCatchUp NewCatchUp(WatchTestStack stack, ILogger<WatchCatchUp>? logger = null) =>
        new(stack.Pipeline, stack.Store, stack.ScanGuard, stack.ScanLease, stack.Time,
            logger ?? NullLogger<WatchCatchUp>.Instance);

    private static void Stamp(string path, DateTimeOffset at) => SetLastWriteTimeUtc(path, at.UtcDateTime);

    [Fact]
    public void EnumerateFiles_NoWatermark_ReturnsEveryFile()
    {
        using var dir = TempDir.New("catchup-all");
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        WriteAllText(a, "zephyrone");
        WriteAllText(b, "zephyrtwo");

        var files = WatchCatchUp.EnumerateFiles(dir.Path, null).ToList();

        files.ShouldContain(a);
        files.ShouldContain(b);
    }

    [Fact]
    public void EnumerateFiles_WithWatermark_ReturnsOnlyFilesWithMtimeAfterIt()
    {
        using var dir = TempDir.New("catchup-since");
        var watermark = new DateTimeOffset(2026, 1, 15, 11, 59, 0, TimeSpan.Zero);
        var older = dir.File("older.md");
        var newer = dir.File("newer.md");
        WriteAllText(older, "zephyrone");
        WriteAllText(newer, "zephyrtwo");
        Stamp(older, watermark.AddHours(-1));
        Stamp(newer, watermark.AddHours(1));

        var files = WatchCatchUp.EnumerateFiles(dir.Path, watermark.ToUnixTimeSeconds()).ToList();

        files.ShouldContain(newer);
        files.ShouldNotContain(older);
    }

    [Fact]
    public void EnumerateFiles_FileWithMtimeEqualToWatermark_IsExcluded()
    {
        using var dir = TempDir.New("catchup-equal");
        var watermark = new DateTimeOffset(2026, 1, 15, 11, 59, 0, TimeSpan.Zero);
        var file = dir.File("equal.md");
        WriteAllText(file, "zephyrone");
        Stamp(file, watermark);

        WatchCatchUp.EnumerateFiles(dir.Path, watermark.ToUnixTimeSeconds()).ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateFiles_OnAFileTarget_ReturnsTheFileWhenItIsDue()
    {
        using var dir = TempDir.New("catchup-file-target");
        var file = dir.File("a.md");
        WriteAllText(file, "zephyrone");
        Stamp(file, new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

        WatchCatchUp.EnumerateFiles(file, null).ShouldContain(file);
        WatchCatchUp.EnumerateFiles(file,
                new DateTimeOffset(2026, 1, 15, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds())
            .ShouldContain(file);
        WatchCatchUp.EnumerateFiles(file,
                new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds())
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task EnqueueInitialScan_OnSingleFileTarget_IngestsTheFile()
    {
        using var dir = TempDir.New("catchup-single");
        var file = dir.File("a.md");
        await WriteAllTextAsync(file, "zephyrone", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        // The fake's Ingested list is not thread-safe; serialize digests (concurrency 1 is a
        // valid config — the concurrency behavior itself is pinned by S4's scheduler tests).
        stack.Memory.Settings[WatchConfigKeys.ConcurrencyProject(Project)] = "1";
        await stack.Service.AddAsync(Project, file, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, file, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(file);
    }

    [Fact]
    public async Task EnqueueInitialScan_IngestsEveryFile_AndAdvancesTheWatermark()
    {
        using var dir = TempDir.New("catchup-full");
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        await WriteAllTextAsync(a, "zephyrone", TestContext.Current.CancellationToken);
        await WriteAllTextAsync(b, "zephyrtwo", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        // The fake's Ingested list is not thread-safe; serialize digests (concurrency 1 is a
        // valid config — the concurrency behavior itself is pinned by S4's scheduler tests).
        stack.Memory.Settings[WatchConfigKeys.ConcurrencyProject(Project)] = "1";
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        var scan = catchUp.LastScan.ShouldNotBeNull();
        await scan;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(a);
        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(b);
        stack.Store.Watches[(Project, dir.Path)].LastChangeTs.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task EnqueueChangedSince_QueuesOnlyFilesWithMtimeAfterTheWatermark()
    {
        using var dir = TempDir.New("catchup-since-scan");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var watermark = stack.Time.GetUtcNow().ToUnixTimeSeconds();
        var older = dir.File("older.md");
        var newer = dir.File("newer.md");
        await WriteAllTextAsync(older, "zephyrone", TestContext.Current.CancellationToken);
        await WriteAllTextAsync(newer, "zephyrtwo", TestContext.Current.CancellationToken);
        Stamp(older, DateTimeOffset.FromUnixTimeSeconds(watermark - 3600));
        Stamp(newer, DateTimeOffset.FromUnixTimeSeconds(watermark + 3600));
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueChangedSince(Project, dir.Path, watermark, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Select(i => i.Path).ShouldContain(newer);
        stack.Memory.Ingested.Select(i => i.Path).ShouldNotContain(older);
    }

    [Fact]
    public async Task EnqueueInitialScan_ReturnsBeforeTheScanRuns()
    {
        using var dir = TempDir.New("catchup-async");
        for (var i = 0; i < 200; i++)
        {
            await WriteAllTextAsync(dir.File($"f{i:D3}.md"), $"zephyrword{i}", TestContext.Current.CancellationToken);
        }

        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        // The fake's Ingested list is not thread-safe; serialize digests (concurrency 1 is a
        // valid config — the concurrency behavior itself is pinned by S4's scheduler tests).
        stack.Memory.Settings[WatchConfigKeys.ConcurrencyProject(Project)] = "1";
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);

        // The call must not run the scan inline: the scan task is created, not awaited.
        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        var scan = catchUp.LastScan.ShouldNotBeNull();
        stack.Memory.Ingested.ShouldBeEmpty();

        await scan;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Count.ShouldBe(200);
    }

    [Fact]
    public async Task EnqueueInitialScan_WithACancelledToken_EnqueuesNothing()
    {
        using var dir = TempDir.New("catchup-precancelled");
        var file = dir.File("a.md");
        await WriteAllTextAsync(file, "zephyrone", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        catchUp.EnqueueInitialScan(Project, dir.Path, cts.Token);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnqueueInitialScan_CancelledMidScan_DoesNotLogAScanError()
    {
        using var dir = TempDir.New("catchup-cancel-midscan");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var logger = new FakeLogger<WatchCatchUp>();
        var catchUp = NewCatchUp(stack, logger);
        var insideListFiles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Store.OnListFiles = async () =>
        {
            insideListFiles.TrySetResult();
            await releaseGate.Task;
        };

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await insideListFiles.Task.WaitAsync(TestContext.Current.CancellationToken);
        catchUp.CancelAllScans();
        releaseGate.SetResult();
        await catchUp.LastScan!;

        var records = logger.Collector.GetSnapshot();
        records.ShouldContain(r => r.Id.Id == 311);
        records.ShouldNotContain(r => r.Id.Id == 310);
    }

    /// <summary>R1: a scan cancelled mid-flight must still release its lease in `finally` with a
    /// non-cancellable token — otherwise the watch is unscannable until the TTL expires.</summary>
    [Fact]
    public async Task ScanCore_WhenTheScanIsCancelled_StillReleasesTheLease()
    {
        using var dir = TempDir.New("catchup-cancel-releases-lease");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);
        var insideListFiles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Store.OnListFiles = async () =>
        {
            insideListFiles.TrySetResult();
            await releaseGate.Task;
        };

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await insideListFiles.Task.WaitAsync(TestContext.Current.CancellationToken);
        catchUp.CancelAllScans();
        releaseGate.SetResult();
        await catchUp.LastScan!;

        stack.ScanLease.ReleaseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueInitialScan_WhenTheLeaseIsHeldElsewhere_EnqueuesNothing()
    {
        using var dir = TempDir.New("catchup-lease-denied");
        WriteAllText(dir.File("a.md"), "zephyrone");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        stack.ScanLease.AcquireResult = false;
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        stack.Store.ListFilesCalls.ShouldBe(0);
    }

    /// <summary>A denied acquire must not release: the lease belongs to the process that holds it.</summary>
    [Fact]
    public async Task EnqueueInitialScan_WhenTheLeaseIsHeldElsewhere_DoesNotReleaseTheHoldersLease()
    {
        using var dir = TempDir.New("catchup-lease-no-release");
        WriteAllText(dir.File("a.md"), "zephyrone");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        stack.ScanLease.AcquireResult = false;
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;

        stack.ScanLease.ReleaseCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EnqueueInitialScan_WhenTheLeaseIsLostMidScan_StopsEnqueuing()
    {
        using var dir = TempDir.New("catchup-lease-lost");
        WriteAllText(dir.File("a.md"), "zephyrone");
        WriteAllText(dir.File("b.md"), "zephyrtwo");
        WriteAllText(dir.File("c.md"), "zephyrthree");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        // Every clock read jumps a full TTL, so the first per-file renewal check fires.
        stack.Time.AutoAdvanceAmount = SqliteWatchScanLease.LeaseTtl;
        stack.ScanLease.RenewResults.Enqueue(false);
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        stack.ScanLease.ReleaseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueInitialScan_WhileTheLeaseKeepsRenewing_IngestsEveryFile()
    {
        using var dir = TempDir.New("catchup-lease-renewed");
        WriteAllText(dir.File("a.md"), "zephyrone");
        WriteAllText(dir.File("b.md"), "zephyrtwo");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        stack.Time.AutoAdvanceAmount = SqliteWatchScanLease.LeaseTtl;
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Count.ShouldBe(2);
    }

    /// <summary>
    ///     The incident's whole shape in one test: a scan is running, the watch is removed, and it
    ///     is added straight back. The first scan must be cancelled, its already-queued events must
    ///     not digest, and exactly one new scan must run — leaving one ingest per file, not two.
    /// </summary>
    [Fact]
    public async Task RemoveThenReAdd_WhileTheFirstScanIsRunning_CancelsItAndRunsExactlyOneNewScan()
    {
        using var dir = TempDir.New("catchup-remove-readd");
        WriteAllText(dir.File("a.md"), "zephyrone");
        WriteAllText(dir.File("b.md"), "zephyrtwo");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);
        var insideScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stack.Store.OnListFiles = async () =>
        {
            insideScan.TrySetResult();
            await holdScan.Task;
        };

        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await insideScan.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Removal cancels the in-flight scan and unregisters the path, so anything the first scan
        // already queued is dropped at the next tick rather than digested under a dead watch.
        await stack.Service.RemoveAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        holdScan.SetResult();
        await catchUp.LastScan!;
        stack.Store.ListFilesTokens[0].IsCancellationRequested.ShouldBeTrue("removal must cancel the running scan");
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.ShouldBeEmpty("events from the cancelled scan must not digest");

        stack.Store.OnListFiles = null;
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        catchUp.EnqueueInitialScan(Project, dir.Path, TestContext.Current.CancellationToken);
        await catchUp.LastScan!;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.ScanGuard.StartedScans.ShouldBe(2, "one scan before removal, one after the re-add — never three");
        stack.Memory.Ingested.Count.ShouldBe(2, "each file must be ingested exactly once");
    }
}
