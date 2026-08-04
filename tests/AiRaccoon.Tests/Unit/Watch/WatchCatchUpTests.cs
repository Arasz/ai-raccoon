using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

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

    private static WatchCatchUp NewCatchUp(WatchTestStack stack) =>
        new(stack.Pipeline, NullLogger<WatchCatchUp>.Instance);

    private static void Stamp(string path, DateTimeOffset at) =>
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);

    [Fact]
    public void EnumerateFiles_NoWatermark_ReturnsEveryFile()
    {
        using var dir = TempDir.New("catchup-all");
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        File.WriteAllText(a, "zephyrone");
        File.WriteAllText(b, "zephyrtwo");

        var files = WatchCatchUp.EnumerateFiles(dir.Path, sinceWatermark: null).ToList();

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
        File.WriteAllText(older, "zephyrone");
        File.WriteAllText(newer, "zephyrtwo");
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
        File.WriteAllText(file, "zephyrone");
        Stamp(file, watermark);

        WatchCatchUp.EnumerateFiles(dir.Path, watermark.ToUnixTimeSeconds()).ShouldBeEmpty();
    }

    [Fact]
    public async Task EnqueueInitialScan_IngestsEveryFile_AndAdvancesTheWatermark()
    {
        using var dir = TempDir.New("catchup-full");
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        File.WriteAllText(a, "zephyrone");
        File.WriteAllText(b, "zephyrtwo");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        // The fake's Ingested list is not thread-safe; serialize digests (concurrency 1 is a
        // valid config — the concurrency behavior itself is pinned by S4's scheduler tests).
        stack.Memory.Settings[WatchConfigKeys.ConcurrencyProject(Project)] = "1";
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueInitialScan(Project, dir.Path);
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
        File.WriteAllText(older, "zephyrone");
        File.WriteAllText(newer, "zephyrtwo");
        Stamp(older, DateTimeOffset.FromUnixTimeSeconds(watermark - 3600));
        Stamp(newer, DateTimeOffset.FromUnixTimeSeconds(watermark + 3600));
        var catchUp = NewCatchUp(stack);

        catchUp.EnqueueChangedSince(Project, dir.Path, watermark);
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
            File.WriteAllText(dir.File($"f{i:D3}.md"), $"zephyrword{i}");
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
        catchUp.EnqueueInitialScan(Project, dir.Path);
        var scan = catchUp.LastScan.ShouldNotBeNull();
        stack.Memory.Ingested.ShouldBeEmpty();

        await scan;
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Memory.Ingested.Count.ShouldBe(200);
    }
}
