using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>A file no corpus routes is classified from a prefix and never hashed, replaced or fingerprinted.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchUnroutedFileTests
{
    private const string Project = "acme";

    [Fact]
    public async Task Digest_UnroutedTextFile_IsUnsupported_AndNeverReplacedOrFingerprinted()
    {
        using var dir = TempDir.New("unrouted-text");
        var file = dir.File("tool.xyz");
        await File.WriteAllTextAsync(file, "plain words", TestContext.Current.CancellationToken);
        var stack = await StackAsync(dir);

        var skipped = await DigestAsync(stack, dir, file);

        skipped.ShouldBe(SkipReason.UnsupportedType);
        stack.Memory.ReplaceCalls.ShouldBe(0);
        stack.Store.UpsertFileHashCalls.ShouldBe(0);
        stack.Store.PeekFingerprint(Project, file).ShouldBeNull();
    }

    [Fact]
    public async Task Digest_UnroutedFileWithNul_IsBinary_AndNeverReplacedOrFingerprinted()
    {
        using var dir = TempDir.New("unrouted-binary");
        var file = dir.File("clip.mp4");
        await File.WriteAllBytesAsync(file, [0x00, 0x01, 0x02, 0x03], TestContext.Current.CancellationToken);
        var stack = await StackAsync(dir);

        var skipped = await DigestAsync(stack, dir, file);

        skipped.ShouldBe(SkipReason.Binary);
        stack.Memory.ReplaceCalls.ShouldBe(0);
        stack.Store.UpsertFileHashCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Digest_UnroutedFileWithNulPastThePrefix_IsUnsupportedNotBinary()
    {
        using var dir = TempDir.New("unrouted-late-nul");
        var file = dir.File("big.xyz");
        await File.WriteAllTextAsync(file, new string('a', BinaryContent.InspectedLength) + "\0tail",
            TestContext.Current.CancellationToken);
        var stack = await StackAsync(dir);

        (await DigestAsync(stack, dir, file)).ShouldBe(SkipReason.UnsupportedType);
    }

    [Fact]
    public async Task Digest_UnroutedFileThatHadRows_RemovesThemAndTheFingerprint()
    {
        using var dir = TempDir.New("unrouted-stale");
        var file = dir.File("was-routed.xyz");
        await File.WriteAllTextAsync(file, "once indexed", TestContext.Current.CancellationToken);
        var stack = await StackAsync(dir);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        stack.Store.SetFingerprint(Project, file, "old-hash", 1);

        await DigestAsync(stack, dir, file);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        stack.Store.PeekFingerprint(Project, file).ShouldBeNull();
    }

    [Fact]
    public async Task Digest_RoutedFile_StillReadsHashesAndReplaces()
    {
        using var dir = TempDir.New("routed");
        var file = dir.File("note.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = await StackAsync(dir);

        (await DigestAsync(stack, dir, file)).ShouldBeNull();

        stack.Memory.ReplaceCalls.ShouldBe(1);
        stack.Store.PeekFingerprint(Project, file).ShouldNotBeNull();
    }

    private static async Task<WatchTestStack> StackAsync(TempDir dir)
    {
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        return stack;
    }

    private static Task<SkipReason?> DigestAsync(WatchTestStack stack, TempDir dir, string file) =>
        stack.Executor.DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);
}
