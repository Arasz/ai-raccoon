using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Replace-by-path digest: hash-skip (R5), delete-by-source-path, rename semantics (D2).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchDigestExecutorTests
{
    private const string Project = "acme";

    private static WatchDigestExecutor Executor(WatchTestStack stack) => stack.Executor;

    [Fact]
    public async Task Digest_NewFile_IngestsFingerprintsAdvancesWatermark()
    {
        using var dir = TempDir.New("digest-new");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.Memory.Ingested[0].Path.ShouldBe(file);
        stack.Memory.Ingested[0].Content.ShouldBe("hello");
        // Best-effort embed retry net fires after a successful ingest.
        stack.Memory.EmbedCalls.ShouldContain(Project);
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(file, "hello"));
        stack.Store.Watches[(Project, dir.Path)].LastChangeTs.ShouldBe(
            WatchTestStack.FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Digest_UnchangedContent_HashSkips_NoIngestNoFingerprintChange()
    {
        using var dir = TempDir.New("digest-skip");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "same", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        // Metadata-only touch: same content, new event.
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Changed, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldHaveSingleItem();
        // The embed fired on the first (ingesting) digest; the hash-skip must not embed again.
        stack.Memory.EmbedCalls.ShouldHaveSingleItem();
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(file, "same"));
    }

    [Fact]
    public async Task Digest_EmbedFailure_IsTolerated_DigestStillCompletes()
    {
        using var dir = TempDir.New("digest-embed-fail");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack
        {
            Memory = { EmbedError = new InvalidOperationException("embed boom") }
        };
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() => Executor(stack).DigestAsync(Project, dir.Path, file,
            WatchEventKind.Created, null, TestContext.Current.CancellationToken));

        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.Memory.EmbedCalls.ShouldContain(Project);
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(file, "hello"));
    }

    [Fact]
    public async Task Digest_RealContentChange_IsNeverSkipped()
    {
        using var dir = TempDir.New("digest-change");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(file, "v2", TestContext.Current.CancellationToken);
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Changed, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.Count.ShouldBe(2);
        stack.Memory.Ingested[1].Content.ShouldBe("v2");
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(file, "v2"));
    }

    [Fact]
    public async Task Digest_FileGone_DeletesChunksAndFingerprint()
    {
        using var dir = TempDir.New("digest-delete");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "bye", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        File.Delete(file);
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Deleted, null,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBeNull();
        stack.Memory.Ingested.ShouldHaveSingleItem();
        // The embed fired on the create-digest only; the delete-digest must not embed.
        stack.Memory.EmbedCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Digest_DeleteOfNeverIngestedFile_IsSilentForMemory()
    {
        using var dir = TempDir.New("digest-delete-never");
        var file = dir.File("never.md");
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Deleted, null,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        stack.Memory.Ingested.ShouldBeEmpty();
    }

    [Fact]
    public async Task Digest_Rename_RemovesOldPathChunksAndDigestsNewPath()
    {
        using var dir = TempDir.New("digest-rename");
        var oldFile = dir.File("old.md");
        var newFile = dir.File("new.md");
        await File.WriteAllTextAsync(oldFile, "moved", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await Executor(stack).DigestAsync(Project, dir.Path, oldFile, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        File.Move(oldFile, newFile);
        await Executor(stack).DigestAsync(Project, dir.Path, newFile, WatchEventKind.Renamed, oldFile,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, oldFile));
        stack.Memory.Ingested.Count.ShouldBe(2);
        stack.Memory.Ingested[1].Path.ShouldBe(newFile);
        stack.Memory.Ingested[1].Content.ShouldBe("moved");
        (await stack.Store.GetFileHashAsync(Project, oldFile, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await stack.Store.GetFileHashAsync(Project, newFile, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(newFile, "moved"));
    }

    [Fact]
    public async Task Digest_RenameOntoExisting_Overwrites_TargetChunksReplaced()
    {
        using var dir = TempDir.New("digest-rename-overwrite");
        var aFile = dir.File("a.md");
        var bFile = dir.File("b.md");
        await File.WriteAllTextAsync(aFile, "from-a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(bFile, "old-b", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await Executor(stack).DigestAsync(Project, dir.Path, bFile, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        File.Move(aFile, bFile, overwrite: true);
        await Executor(stack).DigestAsync(Project, dir.Path, bFile, WatchEventKind.Renamed, aFile,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, aFile));
        stack.Memory.DeletedPaths.ShouldContain((Project, bFile));
        stack.Memory.Ingested.Count.ShouldBe(2);
        stack.Memory.Ingested[^1].Content.ShouldBe("from-a");
        (await stack.Store.GetFileHashAsync(Project, bFile, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(bFile, "from-a"));
    }

    [Fact]
    public async Task Digest_RenameOntoIdenticalContent_HashSkipsTarget_ButOldPathStillRemoved()
    {
        using var dir = TempDir.New("digest-rename-same");
        var aFile = dir.File("a.md");
        var bFile = dir.File("b.md");
        await File.WriteAllTextAsync(aFile, "identical", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(bFile, "identical", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await Executor(stack).DigestAsync(Project, dir.Path, bFile, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        File.Move(aFile, bFile, overwrite: true);
        await Executor(stack).DigestAsync(Project, dir.Path, bFile, WatchEventKind.Renamed, aFile,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, aFile));
        stack.Memory.Ingested.ShouldHaveSingleItem();
    }

    [Fact]
    public void ComputeHash_IsSha256OfPathPlusContent()
    {
        var hash = WatchDigestExecutor.ComputeHash("/repo/a.md", "x");
        hash.Length.ShouldBe(64);
        hash.ShouldMatch(@"^[0-9a-f]{64}$");
        hash.ShouldNotBe(WatchDigestExecutor.ComputeHash("/repo/b.md", "x"));
        hash.ShouldNotBe(WatchDigestExecutor.ComputeHash("/repo/a.md", "y"));
        hash.ShouldBe(WatchDigestExecutor.ComputeHash("/repo/a.md", "x"));
    }
}
