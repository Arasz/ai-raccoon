using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
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

    /// <summary>E6: the digest never embeds itself — it leaves the row pending and signals the
    /// embed topic's single consumer (EmbedDrainService) exactly once.</summary>
    [Fact]
    public async Task Digest_LeavesRowsPending_AndSignalsTheDrain()
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
        stack.EmbedDrainPump.EnqueuedCount.ShouldBe(1);
        var queued = stack.EmbedDrainPump.DrainUpTo(1).ShouldHaveSingleItem();
        queued.Corpus.ShouldBe(EmbedCorpus.Memory);
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
        // The signal fired on the first (ingesting) digest; the hash-skip must not signal again.
        stack.EmbedDrainPump.EnqueuedCount.ShouldBe(1);
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBe(
            WatchDigestExecutor.ComputeHash(file, "same"));
    }

    /// <summary>A Memory signal already queued (not yet taken) coalesces the digest's own enqueue
    /// away — it must never fail the digest. TryEnqueue cannot throw, so this exercises the real
    /// degenerate case (coalesced, counted) rather than an injected exception.</summary>
    [Fact]
    public async Task Digest_SignalAlreadyQueued_CoalescesAndIsTolerated_DigestStillCompletes()
    {
        using var dir = TempDir.New("digest-pump-coalesced");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        stack.EmbedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();

        await Should.NotThrowAsync(() => Executor(stack).DigestAsync(Project, dir.Path, file,
            WatchEventKind.Created, null, TestContext.Current.CancellationToken));

        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.EmbedDrainPump.CoalescedCount.ShouldBe(1);
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
        // The signal fired on the create-digest only; the delete-digest must not signal.
        stack.EmbedDrainPump.EnqueuedCount.ShouldBe(1);
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
    public async Task Digest_IgnoredFile_NotIngested_NotFingerprinted_UpdatesLastChangeOnly()
    {
        using var dir = TempDir.New("digest-ignored");
        var file = dir.File("secret.md");
        await File.WriteAllTextAsync(file, "hush", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.IgnoreRules.Set(dir.Path, "secret.md");
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBeNull();
        stack.Store.Watches[(Project, dir.Path)].LastChangeTs.ShouldBe(WatchTestStack.FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Digest_PreviouslyIndexedFile_ThenIgnoreLineAdded_ThenDigest_DeletesStaleChunksAndFingerprint()
    {
        using var dir = TempDir.New("digest-newly-ignored");
        var file = dir.File("was-tracked.md");
        await File.WriteAllTextAsync(file, "content", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);
        stack.Memory.Ingested.ShouldHaveSingleItem();

        // The ignore line is added AFTER the file was already indexed.
        stack.IgnoreRules.Set(dir.Path, "was-tracked.md");
        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Changed, null,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // Digest_ExplicitMemoryIngestFile_OfAnIgnoredPath_Unaffected_IgnoreAppliesOnlyToTheWatchPath
    // removed (B2 finding, small item 3): it set no ignore rules and exercised WatchDigestExecutor
    // (the watch pipeline, which already resolves its own correct watch-scoped ignore root) rather
    // than the explicit `memory_ingest_file` pipeline its name and comment described — so it could
    // never fail on the contract it claimed to pin, and that claimed contract (explicit
    // memory_ingest_file bypassing a watch's ignore rules) is exactly what §2.1/B2 forbids: ignore
    // now wins for BOTH pipelines. The correct-contract witness for explicit memory_ingest_file
    // honoring ignore rules lives with FileIngestor, which owns that pipeline:
    // FileIngestorCodeRoutingTests.IngestFileAsync_ExplicitlyIgnoredMemoryFile_UnderWatchRoot_ReturnsZeroChunks.

    [Fact]
    public async Task Digest_IgnoreFileItself_IsNeverMatchedAgainstItsOwnRules()
    {
        using var dir = TempDir.New("digest-ignore-self");
        var ignoreFile = dir.File(IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(ignoreFile, "*\n", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.IgnoreRules.Set(dir.Path, "*");
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, ignoreFile, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        // Unindexable extension either way, but the digest must still fingerprint it (never
        // routed through the "ignored, no fingerprint" branch) so future edits are detected.
        (await stack.Store.GetFileHashAsync(Project, ignoreFile, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Digest_IgnoreFileEdited_TriggersAFullRescan()
    {
        using var dir = TempDir.New("digest-ignore-edit-rescan");
        var ignoreFile = dir.File(IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(ignoreFile, "bin/\n", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, ignoreFile, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.ScanInitiator.Calls.ShouldContain((Project, dir.Path));
    }

    [Fact]
    public async Task Digest_IgnoreFileDeleted_TriggersAFullRescan()
    {
        using var dir = TempDir.New("digest-ignore-delete-rescan");
        var ignoreFile = dir.File(IgnoreRulesProvider.FileName);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        // File does not exist on disk — mirrors a Deleted event for the ignore file.
        await Executor(stack).DigestAsync(Project, dir.Path, ignoreFile, WatchEventKind.Deleted, null,
            TestContext.Current.CancellationToken);

        stack.ScanInitiator.Calls.ShouldContain((Project, dir.Path));
    }

    [Fact]
    public async Task Digest_UnrelatedFileEdited_NeverTriggersARescan()
    {
        using var dir = TempDir.New("digest-no-spurious-rescan");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.ScanInitiator.Calls.ShouldBeEmpty();
    }

    /// <summary>
    ///     #494: the enumeration skipped hidden/deny-set segments but the digest did not, so any
    ///     file written under `.claude/worktrees/` or `node_modules/` after registration was indexed
    ///     anyway. An excluded event is now handled exactly like an `ai-raccoon.ignore` match.
    /// </summary>
    [Theory]
    [InlineData(".hidden/x.md")]
    [InlineData("node_modules/y.js")]
    [InlineData(".claude/worktrees/z/doc.md")]
    public async Task Digest_PathUnderAHiddenOrDeniedDirectory_IsNeverIngestedOrFingerprinted(string relative)
    {
        using var dir = TempDir.New("digest-excluded");
        var file = Nested(dir, relative, "worktree copy");
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldBeEmpty();
        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBeNull();
        stack.Store.Watches[(Project, dir.Path)].LastChangeTs.ShouldBe(WatchTestStack.FixedNow.ToUnixTimeSeconds());
    }

    /// <summary>Negative control for the exclusion gate: an ordinary sibling still ingests.</summary>
    [Fact]
    public async Task Digest_SiblingOfAnExcludedDirectory_StillIngests()
    {
        using var dir = TempDir.New("digest-excluded-control");
        Nested(dir, "node_modules/y.js", "vendored");
        var visible = Nested(dir, "docs/keep.md", "zephyrkeep");
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, visible, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldHaveSingleItem();
        stack.Memory.Ingested[0].Path.ShouldBe(visible);
        (await stack.Store.GetFileHashAsync(Project, visible, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Digest_FileIndexedBeforeTheExclusionGateExisted_DeletesStaleChunksAndFingerprint()
    {
        using var dir = TempDir.New("digest-excluded-cleanup");
        var file = Nested(dir, ".claude/worktrees/z/doc.md", "leaked copy");
        var stack = new WatchTestStack();
        stack.Memory.OnDeletePath = stack.Store.RemoveFingerprint;
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);
        await stack.Store.UpsertFileHashAsync(Project, file, "stale-hash", 0,
            TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, dir.Path, file, WatchEventKind.Deleted, null,
            TestContext.Current.CancellationToken);

        stack.Memory.DeletedPaths.ShouldContain((Project, file));
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>
    ///     The rule is the directory walk's, so a watch registered ON a hidden file is still
    ///     digested — the same target distinction <c>WatchCatchUp.EnumerateFiles</c> already makes.
    /// </summary>
    [Fact]
    public async Task Digest_SingleFileWatchOnAHiddenFile_StillIngests()
    {
        using var dir = TempDir.New("digest-hidden-file-target");
        var file = dir.File(".notes.md");
        await File.WriteAllTextAsync(file, "hello", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, file, 0, 0, TestContext.Current.CancellationToken);

        await Executor(stack).DigestAsync(Project, file, file, WatchEventKind.Created, null,
            TestContext.Current.CancellationToken);

        stack.Memory.Ingested.ShouldHaveSingleItem();
        (await stack.Store.GetFileHashAsync(Project, file, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private static string Nested(TempDir dir, string relative, string content)
    {
        var path = Path.Combine(dir.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
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
