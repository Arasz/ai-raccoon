using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Files a digest skipped are listed per watch, leave the list once resolved, and are never fingerprinted.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchPipelineSkipTests
{
    private const string Project = "acme";

    [Fact]
    public async Task Status_UnsupportedAndBinaryFiles_AreListedWithReasons_AndTheWatchStaysHealthy()
    {
        using var dir = TempDir.New("skip-report");
        var stack = new WatchTestStack();
        var tool = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        var image = await SkippedFileAsync(stack, dir, "image.dat", SkipReason.Binary);
        await File.WriteAllBytesAsync(image, [0x00, 0x01], TestContext.Current.CancellationToken);
        await WriteAsync(dir.File("ok.md"), "fine");
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        Enqueue(stack, tool, image, dir.File("ok.md"));

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var status = stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem();
        status.State.ShouldBe(WatchState.Healthy);
        status.SkippedCount.ShouldBe(2);
        status.Skipped.ShouldBe([new SkippedFile(image, SkipReason.Binary), new SkippedFile(tool, SkipReason.UnsupportedType)]);
        stack.Store.PeekFingerprint(Project, tool).ShouldBeNull();
        stack.Store.PeekFingerprint(Project, image).ShouldBeNull();
    }

    [Fact]
    public async Task ServiceStatus_ReportsTheWatchsSkippedFiles()
    {
        using var dir = TempDir.New("skip-service");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        var tool = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        Enqueue(stack, tool);
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var status = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        status.SkippedCount.ShouldBe(1);
        status.Skipped.ShouldBe([new SkippedFile(tool, SkipReason.UnsupportedType)]);
    }

    [Fact]
    public async Task Status_SkippedFileThatBecomesIngestable_LeavesTheList()
    {
        using var dir = TempDir.New("skip-then-supported");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "nb.ipynb", SkipReason.Unparseable);
        await RecordSkipAsync(stack, dir, file);

        stack.Memory.SkipReasons.Remove(file);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        AssertNoSkips(stack);
        stack.Memory.Ingested.ShouldContain(i => i.Path == file);
    }

    [Fact]
    public async Task Status_SkippedFileDeleted_LeavesTheList()
    {
        using var dir = TempDir.New("skip-then-deleted");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        await RecordSkipAsync(stack, dir, file);

        File.Delete(file);
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Deleted));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        AssertNoSkips(stack);
    }

    [Fact]
    public async Task Status_DirectoryOfSkippedFilesDeleted_ItsFilesLeaveTheList()
    {
        using var dir = TempDir.New("skip-dir-deleted");
        var stack = new WatchTestStack();
        var sub = dir.File("assets");
        Directory.CreateDirectory(sub);
        var file = await SkippedFileAsync(stack, dir, Path.Combine("assets", "tool.xyz"), SkipReason.UnsupportedType);
        await RecordSkipAsync(stack, dir, file);

        Directory.Delete(sub, true);
        stack.Pipeline.Enqueue(new WatchEvent(Project, sub, WatchEventKind.Deleted));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        AssertNoSkips(stack);
    }

    [Fact]
    public async Task Status_SkippedFileRenamed_OldPathLeavesTheList()
    {
        using var dir = TempDir.New("skip-then-renamed");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        await RecordSkipAsync(stack, dir, file);

        var renamed = dir.File("tool.md");
        File.Move(file, renamed);
        stack.Pipeline.Enqueue(new WatchEvent(Project, renamed, WatchEventKind.Renamed, file));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        AssertNoSkips(stack);
    }

    [Fact]
    public async Task Status_SkippedFileLaterExcluded_LeavesTheList()
    {
        using var dir = TempDir.New("skip-then-excluded");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        await RecordSkipAsync(stack, dir, file);

        stack.IgnoreRules.Set(dir.Path, "*.xyz");
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        AssertNoSkips(stack);
    }

    [Fact]
    public async Task Status_UnregisterWatch_ClearsItsSkips()
    {
        using var dir = TempDir.New("skip-unregister");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "tool.xyz", SkipReason.UnsupportedType);
        await RecordSkipAsync(stack, dir, file);

        stack.Pipeline.UnregisterWatch(Project, dir.Path);
        stack.Pipeline.RegisterWatch(Project, dir.Path);

        AssertNoSkips(stack);
    }

    [Fact]
    public async Task Status_UnchangedSkippedFile_IsDigestedAgain_AndStillReported()
    {
        using var dir = TempDir.New("skip-retry");
        var stack = new WatchTestStack();
        var file = await SkippedFileAsync(stack, dir, "nb.ipynb", SkipReason.Unparseable);
        await RecordSkipAsync(stack, dir, file);
        stack.Memory.SkipReasons.Remove(file);

        Enqueue(stack, file);
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldContain(i => i.Path == file, "no fingerprint, so unchanged content is not hash-skipped");
    }

    [Fact]
    public async Task Status_MoreThanTwentySkips_ListsTheFirstTwentyByPath_AndCountsAll()
    {
        using var dir = TempDir.New("skip-cap");
        var stack = new WatchTestStack();
        var files = new List<string>();
        for (var i = 24; i >= 0; i--)
        {
            files.Add(await SkippedFileAsync(stack, dir, $"f{i:D2}.xyz", SkipReason.UnsupportedType));
        }

        stack.Pipeline.RegisterWatch(Project, dir.Path);
        Enqueue(stack, [.. files]);

        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var status = stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem();
        status.SkippedCount.ShouldBe(25);
        status.Skipped.Select(s => s.Path).ShouldBe(files.Order(StringComparer.Ordinal).Take(20));
    }

    private static async Task<string> SkippedFileAsync(WatchTestStack stack, TempDir dir, string name, SkipReason reason)
    {
        var path = dir.File(name);
        await WriteAsync(path, name);
        stack.Memory.SkipReasons[path] = reason;
        return path;
    }

    /// <summary>Digests one skipped file under a fresh watch and checks the skip was recorded.</summary>
    private static async Task RecordSkipAsync(WatchTestStack stack, TempDir dir, string file)
    {
        stack.Pipeline.RegisterWatch(Project, dir.Path);
        Enqueue(stack, file);
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);
        stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem().SkippedCount.ShouldBe(1);
    }

    private static Task WriteAsync(string path, string content) =>
        File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

    private static void Enqueue(WatchTestStack stack, params string[] paths)
    {
        foreach (var path in paths)
        {
            stack.Pipeline.Enqueue(new WatchEvent(Project, path, WatchEventKind.Created));
        }
    }

    private static void AssertNoSkips(WatchTestStack stack)
    {
        var status = stack.Pipeline.GetStatuses(Project).ShouldHaveSingleItem();
        status.SkippedCount.ShouldBe(0);
        status.Skipped.ShouldBeEmpty();
    }
}
