using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Real-FS mirror behavior: real temp dirs, real FileSystemWatcher (via WatchEventSource),
///     real SqliteMemoryStore, FakeTimeProvider for the tick. OS event delivery is bounded by
///     polling (≤5s per poll loop; R7); per-OS event coalescing is accepted — the catch-up
///     watermark is the safety net. No embedding provider is configured, so search is FTS-only.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WatchIntegrationTests
{
    private const string Project = "acme";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatedFile_BecomesSearchable()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        stack.Write("a.md", "zephyralpha seed");

        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyralpha", TestContext.Current.CancellationToken))
                    .Any(r => r.SourceFile == stack.File("a.md")), TestContext.Current.CancellationToken))
            .ShouldBeTrue("created file did not become searchable within the poll bound");
    }

    [Fact]
    public async Task HostedService_StartsThePipelineTickLoop_FileBecomesSearchableWithoutManualTicks()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);

        _ = stack.Hosted.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Wait for the hosted reconcile to start the watcher + initial scan.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (stack.CatchUp.LastScan is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            var scan = stack.CatchUp.LastScan.ShouldNotBeNull("the hosted reconcile did not start a catch-up scan");
            await scan;

            stack.Write("a.md", "zephyrloop token");

            // No manual TickOnceAsync anywhere: the pipeline loop's own 1s tick (fired by
            // advancing the fake clock) must drain the channel and digest the created file.
            var pollDeadline = DateTime.UtcNow.AddSeconds(15);
            var searchable = false;
            while (!searchable && DateTime.UtcNow < pollDeadline)
            {
                stack.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(50, TestContext.Current.CancellationToken);
                searchable = (await stack.SearchAsync("zephyrloop", TestContext.Current.CancellationToken))
                    .Any(r => r.SourceFile == stack.File("a.md"));
            }

            searchable.ShouldBeTrue("the hosted service never started the pipeline tick loop");
        }
        finally
        {
            await stack.Hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ChangedFile_ReplacesItsContentInSearch()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        stack.Write("a.md", "zephyralpha v1");
        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyralpha", TestContext.Current.CancellationToken)).Any(),
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("initial content did not become searchable");

        stack.Write("a.md", "zephyrbeta v2");

        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrbeta", TestContext.Current.CancellationToken))
                    .Any(r => r.SourceFile == stack.File("a.md")), TestContext.Current.CancellationToken))
            .ShouldBeTrue("changed content did not become searchable");
        (await stack.SearchAsync("zephyralpha", TestContext.Current.CancellationToken)).Count
            .ShouldBe(0, "mirror semantics: old content must be replaced, not accumulated");
    }

    [Fact]
    public async Task DeletedFile_RemovesItsChunks()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        stack.Write("a.md", "zephyrgamma content");
        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrgamma", TestContext.Current.CancellationToken)).Any(),
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("file did not become searchable");

        File.Delete(stack.File("a.md"));

        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrgamma", TestContext.Current.CancellationToken)).Count == 0,
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("deleted file's content is still searchable");
        (await stack.CountEntriesAsync(stack.File("a.md"), null, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task RenamedFile_MovesItsMemory_AndLeavesNothingUnderTheOldPath()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        stack.Write("old.md", "zephyrdelta content");
        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrdelta", TestContext.Current.CancellationToken))
                    .Any(r => r.SourceFile == stack.File("old.md")), TestContext.Current.CancellationToken))
            .ShouldBeTrue("file did not become searchable");

        File.Move(stack.File("old.md"), stack.File("new.md"));

        (await stack.StepUntilAsync(async () =>
        {
            var results = await stack.SearchAsync("zephyrdelta", TestContext.Current.CancellationToken);
            return results.Any(r => r.SourceFile == stack.File("new.md")) &&
                   results.All(r => r.SourceFile != stack.File("old.md"));
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("rename did not move the content to the new path");
        (await stack.CountEntriesAsync(stack.File("old.md"), null, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task RenameOntoExistingPath_KeepsOnlyTheIncomingContent()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        stack.Write("target.md", "zephyrepilon target");
        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrepilon", TestContext.Current.CancellationToken)).Any(),
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("target content did not become searchable");
        stack.Write("source.md", "zephyrzeta incoming");
        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrzeta", TestContext.Current.CancellationToken)).Any(),
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("source content did not become searchable");

        File.Move(stack.File("source.md"), stack.File("target.md"), overwrite: true);

        (await stack.StepUntilAsync(async () =>
        {
            var results = await stack.SearchAsync("zephyrzeta", TestContext.Current.CancellationToken);
            return results.Any(r => r.SourceFile == stack.File("target.md"));
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("incoming content did not land under the target");
        (await stack.SearchAsync("zephyrepilon", TestContext.Current.CancellationToken)).Count
            .ShouldBe(0, "D2 overwrite: the target's previous content must be replaced");
        (await stack.CountEntriesAsync(stack.File("target.md"), "zephyrepilon", TestContext.Current.CancellationToken))
            .ShouldBe(0);
        (await stack.CountEntriesAsync(stack.File("target.md"), "zephyrzeta", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(1);
        (await stack.CountEntriesAsync(stack.File("source.md"), null, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Restart_ReWatches_AndCatchUpReDigestsChangedFiles_SkippingUnchangedOnes()
    {
        using var first = new Stack(name: "restart", now: FixedNow, deleteDataRoot: false);
        await first.EnableAsync(TestContext.Current.CancellationToken);
        await first.AllowScopeAsync(TestContext.Current.CancellationToken);
        first.Write("a.md", "zephyrone v1");
        first.Write("c.md", "zephyrthree stable");
        first.Age("a.md", TimeSpan.FromMinutes(1));
        first.Age("c.md", TimeSpan.FromMinutes(1));
        await first.AddWatchAsync(TestContext.Current.CancellationToken);
        await first.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (first.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        (await first.StepUntilAsync(async () =>
        {
            var a = await first.SearchAsync("zephyrone", TestContext.Current.CancellationToken);
            var c = await first.SearchAsync("zephyrthree", TestContext.Current.CancellationToken);
            return a.Any() && c.Any();
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("initial scan did not ingest the seed files");

        // "Server down": watchers disposed, no ticks, then downtime passes on the fake clock.
        first.EventSource.StopAll();
        first.Time.Advance(TimeSpan.FromMinutes(5));
        first.Write("a.md", "zephyrtwo v2");
        first.Write("b.md", "zephyrbee new");

        using var second = new Stack(name: "restart", now: first.Time.GetUtcNow(), deleteDataRoot: true);
        await second.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (second.CatchUp.LastScan is { } scan)
        {
            await scan;
        }

        (await second.StepUntilAsync(async () =>
        {
            var changed = await second.SearchAsync("zephyrtwo", TestContext.Current.CancellationToken);
            var newFile = await second.SearchAsync("zephyrbee", TestContext.Current.CancellationToken);
            var stable = await second.SearchAsync("zephyrthree", TestContext.Current.CancellationToken);
            return changed.Any(r => r.SourceFile == second.File("a.md")) &&
                   newFile.Any(r => r.SourceFile == second.File("b.md")) &&
                   stable.Any(r => r.SourceFile == second.File("c.md"));
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("restart catch-up did not re-digest changed files");

        (await second.SearchAsync("zephyrone", TestContext.Current.CancellationToken)).Count
            .ShouldBe(0, "the changed file's old content must be gone after catch-up");
        (await second.CountEntriesAsync(second.File("a.md"), "zephyrone", TestContext.Current.CancellationToken))
            .ShouldBe(0);
        (await second.CountEntriesAsync(second.File("a.md"), "zephyrtwo", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(1);
        (await second.CountEntriesAsync(second.File("c.md"), "zephyrthree", TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(1, "unchanged files are skipped by catch-up but stay searchable");
    }

    [Fact]
    public async Task DeletedDirectory_Cascades_RemovesChunksAndFingerprintsOfNestedFiles()
    {
        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        Directory.CreateDirectory(stack.File("sub"));
        stack.Write("sub/a.md", "zephyrnest one");
        stack.Write("sub/b.md", "zephyrnest two");
        stack.Write("top.md", "zephyrtop root");
        (await stack.StepUntilAsync(async () =>
        {
            var nested = await stack.SearchAsync("zephyrnest", TestContext.Current.CancellationToken);
            var top = await stack.SearchAsync("zephyrtop", TestContext.Current.CancellationToken);
            return nested.Count >= 2 && top.Any();
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("seed files did not become searchable");

        // A directory delete event digests with the DIRECTORY path — the store must remove
        // every chunk under it, not just the exact path.
        await stack.Memory.DeleteSourcePathAsync(Project, stack.File("sub"), TestContext.Current.CancellationToken);

        (await stack.CountEntriesUnderAsync(stack.File("sub"), TestContext.Current.CancellationToken))
            .ShouldBe(0, "nested chunks must be removed by the directory cascade");
        (await stack.CountFingerprintsUnderAsync(stack.File("sub"), TestContext.Current.CancellationToken))
            .ShouldBe(0, "nested fingerprints must be removed by the directory cascade");
        (await stack.SearchAsync("zephyrtop", TestContext.Current.CancellationToken))
            .Any().ShouldBeTrue("a sibling file outside the deleted directory must survive");
    }

    [Fact]
    public async Task Restart_CatchUp_RemovesChunksOfFilesDeletedWhileTheServerWasDown()
    {
        using var first = new Stack(name: "restart-delete", now: FixedNow, deleteDataRoot: false);
        await first.EnableAsync(TestContext.Current.CancellationToken);
        await first.AllowScopeAsync(TestContext.Current.CancellationToken);
        first.Write("a.md", "zephyrdoomed v1");
        first.Write("c.md", "zephyrsurvivor stable");
        first.Age("a.md", TimeSpan.FromMinutes(1));
        first.Age("c.md", TimeSpan.FromMinutes(1));
        await first.AddWatchAsync(TestContext.Current.CancellationToken);
        await first.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (first.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        (await first.StepUntilAsync(async () =>
        {
            var doomed = await first.SearchAsync("zephyrdoomed", TestContext.Current.CancellationToken);
            var survivor = await first.SearchAsync("zephyrsurvivor", TestContext.Current.CancellationToken);
            return doomed.Any() && survivor.Any();
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("seed files did not become searchable");

        // "Server down": the watcher stops, downtime passes, and a file is deleted while down.
        first.EventSource.StopAll();
        first.Time.Advance(TimeSpan.FromMinutes(5));
        File.Delete(first.File("a.md"));

        using var second = new Stack(name: "restart-delete", now: first.Time.GetUtcNow(), deleteDataRoot: true);
        await second.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (second.CatchUp.LastScan is { } scan)
        {
            await scan;
        }

        (await second.StepUntilAsync(async () =>
        {
            var doomed = await second.SearchAsync("zephyrdoomed", TestContext.Current.CancellationToken);
            var survivor = await second.SearchAsync("zephyrsurvivor", TestContext.Current.CancellationToken);
            return doomed.Count == 0 && survivor.Any();
        }, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "catch-up did not remove chunks of a file deleted while the server was down");
    }

    [Fact]
    public async Task Restart_SingleFileWatch_FileChangedWhileDown_IsReIngestedOnCatchUp()
    {
        using var first = new Stack(name: "restart-single", now: FixedNow, deleteDataRoot: false);
        await first.EnableAsync(TestContext.Current.CancellationToken);
        await first.AllowScopeAsync(TestContext.Current.CancellationToken);
        first.Write("readme.md", "zephyrsingle v1");
        first.Age("readme.md", TimeSpan.FromMinutes(1));
        await first.Service.AddAsync(Project, first.File("readme.md"), TestContext.Current.CancellationToken);
        await first.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (first.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        (await first.StepUntilAsync(async () =>
            (await first.SearchAsync("zephyrsingle", TestContext.Current.CancellationToken))
            .Any(r => r.SourceFile == first.File("readme.md")), TestContext.Current.CancellationToken))
            .ShouldBeTrue("the single-file watch did not ingest its file on the initial scan");

        // "Server down": watcher off, downtime passes, the watched file changes while down.
        first.EventSource.StopAll();
        first.Time.Advance(TimeSpan.FromMinutes(5));
        first.Write("readme.md", "zephyrsingle v2");

        using var second = new Stack(name: "restart-single", now: first.Time.GetUtcNow(), deleteDataRoot: true);
        await second.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (second.CatchUp.LastScan is { } scan)
        {
            await scan;
        }

        (await second.StepUntilAsync(async () =>
        {
            var results = await second.SearchAsync("zephyrsingle", TestContext.Current.CancellationToken);
            return results.Any(r => r.SourceFile == second.File("readme.md") &&
                                    r.Snippet.Contains("v2", StringComparison.Ordinal));
        }, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "catch-up did not re-ingest the single-file watch target changed while down");
        (await second.CountEntriesAsync(second.File("readme.md"), "v1", TestContext.Current.CancellationToken))
            .ShouldBe(0, "mirror semantics: the old content must be replaced on re-ingest");
    }

    [Fact]
    public async Task UnreadableFile_DigestFails_StatusShowsError_PipelineKeepsRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            // POSIX permission bits do not exist on Windows (OS matrix deferred — recorded deviations).
            return;
        }

        using var stack = new Stack();
        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);
        stack.Write("secret.md", "zephyrsecret hidden");
        stack.Age("secret.md", TimeSpan.FromMinutes(1));
        File.SetUnixFileMode(stack.File("secret.md"), UnixFileMode.None);

        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } initial)
        {
            await initial;
        }

        (await stack.StepUntilAsync(async () =>
        {
            var status = await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken);
            return status.Count == 1 && status[0].LastError is not null;
        }, TestContext.Current.CancellationToken)).ShouldBeTrue("unreadable digest did not surface an error");
        var failed = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        failed.State.ShouldBe(WatchState.Retrying);
        failed.LastError.ShouldNotBeNull().ShouldContain("denied");

        // The pipeline keeps running: restore permissions, change the file, digest succeeds.
        File.SetUnixFileMode(stack.File("secret.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        stack.Write("secret.md", "zephyrfixed recovered");

        (await stack.StepUntilAsync(
                async () => (await stack.SearchAsync("zephyrfixed", TestContext.Current.CancellationToken)).Any(),
                TestContext.Current.CancellationToken))
            .ShouldBeTrue("pipeline did not recover after the unreadable digest failure");
        var recovered = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        recovered.State.ShouldBe(WatchState.Healthy);
    }

    [Fact]
    public async Task AddOnLargeDirectory_ReturnsImmediately_ScanRunsInBackground_StatusScanning()
    {
        using var stack = new Stack();
        for (var i = 0; i < 300; i++)
        {
            stack.Write($"f{i:D3}.md", $"zephyrword{i} unique");
        }

        for (var i = 0; i < 300; i++)
        {
            stack.Age($"f{i:D3}.md", TimeSpan.FromMinutes(1));
        }

        await stack.EnableAsync(TestContext.Current.CancellationToken);
        await stack.AllowScopeAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await stack.AddWatchAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "add must not run the initial scan inline");
        var status = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        status.State.ShouldBe(WatchState.Scanning);
        (await stack.SearchAsync("zephyrword0", TestContext.Current.CancellationToken)).Count
            .ShouldBe(0, "nothing is ingested until the background scan runs (feature rule 4)");

        await stack.Hosted.ReconcileAsync(TestContext.Current.CancellationToken);
        if (stack.CatchUp.LastScan is { } scan)
        {
            await scan;
        }

        (await stack.StepUntilAsync(async () =>
        {
            var results = await stack.SearchAsync("zephyrword299", TestContext.Current.CancellationToken);
            var current = await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken);
            return results.Any() && current.Count == 1 && current[0].State == WatchState.Healthy;
        }, TestContext.Current.CancellationToken, maxSeconds: 30)).ShouldBeTrue("large-dir scan did not finish");
        (await stack.CountEntriesUnderAsync(stack.WatchDir, TestContext.Current.CancellationToken))
            .ShouldBeGreaterThanOrEqualTo(300);
    }

    /// <summary>Real-FS harness: bank under a temp DataRoot, watched repo dir beside it.</summary>
    private sealed class Stack : IDisposable
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly bool _deleteDataRoot;

        public Stack(string? name = null, DateTimeOffset? now = null, bool deleteDataRoot = true)
        {
            _deleteDataRoot = deleteDataRoot;
            Time = new FakeTimeProvider(now ?? FixedNow);
            DataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-watch-integration",
                name ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataRoot);
            WatchDir = Path.Combine(DataRoot, "repo");
            Directory.CreateDirectory(WatchDir);
            _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
            Memory = new SqliteMemoryStore(_factory, Time, new TokenizerChunker(), new EmbeddingService());
            WatchStore = new WatchStore(_factory);
            var host = new MemoryExtensionHost(Memory, []);
            Pipeline = new WatchPipeline(new WatchScheduler(),
                new WatchDigestExecutor(host, WatchStore, host, Time), new WatchRetryPolicy(), Memory, Time,
                NullLogger<WatchPipeline>.Instance);
            EventSource = new WatchEventSource(Pipeline.Enqueue, Errors.Add,
                NullLogger<WatchEventSource>.Instance);
            CatchUp = new WatchCatchUp(Pipeline, WatchStore, NullLogger<WatchCatchUp>.Instance);
            Hosted = new WatchHostedService(Memory, WatchStore, Pipeline, EventSource, CatchUp, Time,
                NullLogger<WatchHostedService>.Instance);
            Service = new WatchService(WatchStore, Memory, Pipeline, Time);
        }

        public FakeTimeProvider Time { get; }

        public string DataRoot { get; }

        public string WatchDir { get; }

        public SqliteMemoryStore Memory { get; }

        public WatchStore WatchStore { get; }

        public WatchPipeline Pipeline { get; }

        public WatchEventSource EventSource { get; }

        public WatchCatchUp CatchUp { get; }

        public WatchHostedService Hosted { get; }

        public WatchService Service { get; }

        public List<WatchEventError> Errors { get; } = [];

        public string File(string name) => Path.Combine(WatchDir, name);

        public Task EnableAsync(CancellationToken cancellationToken) =>
            Memory.SetSettingAsync(WatchConfigKeys.EnabledProject(Project), "true", cancellationToken);

        public Task AllowScopeAsync(CancellationToken cancellationToken) =>
            Memory.SetSettingAsync(WatchConfigKeys.ScopeProject(Project),
                WatchConfigKeys.SerializeScope([WatchDir]), cancellationToken);

        public Task AddWatchAsync(CancellationToken cancellationToken) =>
            Service.AddAsync(Project, WatchDir, cancellationToken);

        /// <summary>Writes the file and stamps its mtime at the CURRENT fake time.</summary>
        public void Write(string name, string content)
        {
            var path = File(name);
            System.IO.File.WriteAllText(path, content);
            System.IO.File.SetLastWriteTimeUtc(path, Time.GetUtcNow().UtcDateTime);
        }

        /// <summary>Rewinds a file's mtime so catch-up treats it as unchanged since the watermark.</summary>
        public void Age(string name, TimeSpan age)
        {
            var path = File(name);
            System.IO.File.SetLastWriteTimeUtc(path, Time.GetUtcNow().Subtract(age).UtcDateTime);
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Memory.SearchAsync(new SearchQuery(Project, query), cancellationToken);

        public async Task<int> CountEntriesAsync(string path, string? valueContains,
            CancellationToken cancellationToken)
        {
            await using var connection = await _factory.OpenBankAsync(cancellationToken);
            var sql = valueContains is null
                ? "SELECT count(*) FROM entries WHERE project_id = @p AND source_file = @path"
                : "SELECT count(*) FROM entries WHERE project_id = @p AND source_file = @path AND value LIKE @value";
            object parameters = valueContains is null
                ? new { p = Project, path }
                : new { p = Project, path, value = "%" + valueContains + "%" };
            return await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        }

        public async Task<int> CountEntriesUnderAsync(string dirPrefix, CancellationToken cancellationToken)
        {
            await using var connection = await _factory.OpenBankAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM entries WHERE project_id = @p AND source_file LIKE @prefix",
                new { p = Project, prefix = dirPrefix + "%" }, cancellationToken: cancellationToken));
        }

        public async Task<int> CountFingerprintsUnderAsync(string dirPrefix, CancellationToken cancellationToken)
        {
            await using var connection = await _factory.OpenBankAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM watch_files WHERE project_id = @p AND path LIKE @prefix",
                new { p = Project, prefix = dirPrefix + "%" }, cancellationToken: cancellationToken));
        }

        /// <summary>
        ///     Bounded poll: advance the fake clock 100ms, drain the pipeline once, sleep briefly
        ///     for OS event delivery — until the condition holds or the real-time deadline passes.
        /// </summary>
        public async Task<bool> StepUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken,
            int maxSeconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
            while (!await condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                Time.Advance(TimeSpan.FromMilliseconds(100));
                await Pipeline.TickOnceAsync(cancellationToken);
                await Task.Delay(20, cancellationToken);
            }

            return true;
        }

        public void Dispose()
        {
            EventSource.StopAll();
            if (_deleteDataRoot)
            {
                try
                {
                    Directory.Delete(DataRoot, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
