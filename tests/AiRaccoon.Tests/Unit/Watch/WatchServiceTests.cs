using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>IWatchService impl: enable + scope + existence validation, idempotency, normalized identity, status.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchServiceTests
{
    private const string Project = "acme";
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";

    [Fact]
    public async Task AddAsync_WhenWatchingDisabled_ThrowsWatchDisabled()
    {
        using var dir = TempDir.New("service-disabled");
        var stack = new WatchTestStack();
        stack.AllowScope(dir.Path);

        await Should.ThrowAsync<WatchDisabledException>(
            () => stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_PathOutsideScope_ThrowsPathOutsideScope()
    {
        using var dir = TempDir.New("service-scope");
        using var outside = TempDir.New("service-scope-outside");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await Should.ThrowAsync<PathOutsideScopeException>(
            () => stack.Service.AddAsync(Project, outside.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_NonExistentPath_ThrowsPathNotFound()
    {
        using var dir = TempDir.New("service-missing");
        var missing = dir.File("does-not-exist");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await Should.ThrowAsync<PathNotFoundException>(
            () => stack.Service.AddAsync(Project, missing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_ValidPath_RegistersWatch_AndStatusReportsScanning()
    {
        using var dir = TempDir.New("service-add");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.ShouldContainKey((Project, dir.Path));
        var status = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        status.Path.ShouldBe(dir.Path);
        status.State.ShouldBe(WatchState.Scanning);
    }

    [Fact]
    public async Task AddAsync_WhenMigrated_FoldsAKnownLoserToTheWinner()
    {
        // Ledger — raw-loser-watch-create : --filter AddAsync_WhenMigrated_FoldsAKnownLoserToTheWinner : loser create on a migrated bank.
        // d-425 MUST-1: the watch-create boundary folds through the same alias table as the
        // gate — a loser create lands winner-keyed, so post-repair scans ingest under the winner
        // instead of resurrecting the loser. Config already lives under the winner (the P4 key
        // helpers fold at construction).
        using var dir = TempDir.New("service-fold");
        var stack = new WatchTestStack(migrationGate: new MigratedGate());
        stack.Enable(Winner);
        stack.AllowScope(dir.Path, Winner);

        await stack.Service.AddAsync(Loser, dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.ShouldContainKey((Winner, dir.Path));
        stack.Store.Watches.Keys.ShouldAllBe(k => k.ProjectId != Loser,
            "no loser-keyed row survives a migrated create");
        (await stack.Service.StatusAsync(Loser, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        await stack.Service.RemoveAsync(Loser, dir.Path, TestContext.Current.CancellationToken);
        stack.Store.Watches.ShouldBeEmpty("identity agrees on both sides of the marker");
    }

    [Fact]
    public async Task AddAsync_WhenUnmigrated_PassesTheLoserThrough()
    {
        // Ledger — fold-unmigrated-watch-create : --filter AddAsync_WhenUnmigrated_PassesTheLoserThrough : loser create on an unmigrated bank.
        // Pins the rename arrange contract: pre-migration the service stores verbatim (the BDD
        // rename scenarios seed loser watches through this method, then the repair renames them).
        using var dir = TempDir.New("service-unmigrated");
        var stack = new WatchTestStack();
        stack.Enable(Winner);
        stack.AllowScope(dir.Path, Winner);

        await stack.Service.AddAsync(Loser, dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.ShouldContainKey((Loser, dir.Path));
    }

    [Fact]
    public async Task AddAsync_SamePathTwice_IsANoOp_SingleWatch()
    {
        using var dir = TempDir.New("service-twice");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        var second = await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.Count.ShouldBe(1);
        (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        // Equal-path re-add: idempotent, reports absorbedBy (only case that does), never pruned.
        second.AbsorbedBy.ShouldBe(dir.Path);
        second.Pruned.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAsync_BroaderWatch_PrunesTheNarrower_ReportsItInPruned_EntriesStay()
    {
        using var root = TempDir.New("service-overlap-root");
        var inner = Path.Combine(root.Path, "src");
        Directory.CreateDirectory(inner);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(root.Path);
        await stack.Service.AddAsync(Project, inner, TestContext.Current.CancellationToken);

        var outcome = await stack.Service.AddAsync(Project, root.Path, TestContext.Current.CancellationToken);

        outcome.Pruned.ShouldBe([inner]);
        outcome.AbsorbedBy.ShouldBeNull();
        stack.Store.Watches.Count.ShouldBe(1);
        stack.Store.Watches.ShouldContainKey((Project, root.Path));
        stack.Store.Watches.ShouldNotContainKey((Project, inner));
        // Runtime state for the pruned watch is gone; only the broader watch reports status.
        var statuses = await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken);
        statuses.ShouldHaveSingleItem();
        statuses[0].Path.ShouldBe(root.Path);
    }

    [Fact]
    public async Task AddAsync_NarrowerInsideExisting_ThrowsWatchOverlapException_NamingTheCoveringWatch_NothingWritten()
    {
        using var root = TempDir.New("service-overlap-reject-root");
        var inner = Path.Combine(root.Path, "src");
        Directory.CreateDirectory(inner);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(root.Path);
        await stack.Service.AddAsync(Project, root.Path, TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<WatchOverlapException>(
            () => stack.Service.AddAsync(Project, inner, TestContext.Current.CancellationToken));

        ex.CoveringPath.ShouldBe(root.Path);
        stack.Store.Watches.Count.ShouldBe(1);
        stack.Store.Watches.ShouldContainKey((Project, root.Path));
        stack.Store.Watches.ShouldNotContainKey((Project, inner));
    }

    [Fact]
    public async Task AddAsync_DisjointWatch_BothSurvive_NoCrossPrune()
    {
        using var repoA = TempDir.New("service-overlap-repo-a");
        using var repoB = TempDir.New("service-overlap-repo2");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.Memory.Settings[IngestScopeKeys.ScopeProject(Project)] =
            IngestScopeKeys.Serialize([repoA.Path, repoB.Path]);
        await stack.Service.AddAsync(Project, repoA.Path, TestContext.Current.CancellationToken);

        var outcome = await stack.Service.AddAsync(Project, repoB.Path, TestContext.Current.CancellationToken);

        outcome.Pruned.ShouldBeEmpty();
        stack.Store.Watches.Count.ShouldBe(2);
        stack.Store.Watches.ShouldContainKey((Project, repoA.Path));
        stack.Store.Watches.ShouldContainKey((Project, repoB.Path));
    }

    [Fact]
    public async Task AddAsync_DifferentPathNormalization_IsStillANoOp()
    {
        using var dir = TempDir.New("service-normalize");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await stack.Service.AddAsync(Project, dir.Path + Path.DirectorySeparatorChar,
            TestContext.Current.CancellationToken);

        stack.Store.Watches.Count.ShouldBe(1);
        stack.Store.Watches.ShouldContainKey((Project, dir.Path));
    }

    [Fact]
    public async Task AddAsync_SamePathInSecondProject_CreatesASecondWatch()
    {
        using var dir = TempDir.New("service-two-projects");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        stack.Enable("other");
        stack.AllowScope(dir.Path, "other");

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await stack.Service.AddAsync("other", dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveAsync_RemovesWatch_AndDropsPendingDigests()
    {
        using var dir = TempDir.New("service-remove");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Service.RemoveAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        stack.Store.Watches.ShouldBeEmpty();
        stack.Memory.Ingested.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_NonExistentWatch_IsANoOp()
    {
        using var dir = TempDir.New("service-remove-missing");
        var stack = new WatchTestStack();

        await stack.Service.RemoveAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task StatusAsync_ShowsStateLastErrorAndLastSync_AfterDigestLifecycle()
    {
        using var dir = TempDir.New("service-status");
        var file = dir.File("a.md");
        await File.WriteAllTextAsync(file, "v1", TestContext.Current.CancellationToken);
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Memory.IngestError = new IOException("boom");
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Created));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var failed = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        failed.State.ShouldBe(WatchState.Retrying);
        failed.LastError.ShouldNotBeNull().ShouldContain("boom");

        stack.Memory.IngestError = null;
        stack.Pipeline.Enqueue(new WatchEvent(Project, file, WatchEventKind.Changed));
        stack.Time.Advance(WatchRetryPolicy.BackoffFor(1));
        await stack.Pipeline.TickOnceAsync(TestContext.Current.CancellationToken);

        var healthy = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        healthy.State.ShouldBe(WatchState.Healthy);
        healthy.LastError.ShouldBeNull();
        healthy.LastSync.ShouldBe(WatchTestStack.FixedNow + WatchRetryPolicy.BackoffFor(1));
    }

    [Fact]
    public async Task StatusAsync_RegisteredButNotYetSeenByPipeline_DefaultsToScanning()
    {
        using var dir = TempDir.New("service-unseen");
        var stack = new WatchTestStack();
        await stack.Store.AddWatchAsync(Project, dir.Path, 0, 0, TestContext.Current.CancellationToken);

        var status = (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Single();
        status.State.ShouldBe(WatchState.Scanning);
    }

    [Fact]
    public async Task IsEnabledAsync_ReflectsResolvedConfig()
    {
        var stack = new WatchTestStack();
        (await stack.Service.IsEnabledAsync(Project, TestContext.Current.CancellationToken)).ShouldBeFalse();

        stack.Enable();
        (await stack.Service.IsEnabledAsync(Project, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task IsPathAllowedAsync_ReflectsScopeAllowlist()
    {
        using var dir = TempDir.New("service-allowed");
        using var outside = TempDir.New("service-allowed-outside");
        var stack = new WatchTestStack();
        stack.AllowScope(dir.Path);

        (await stack.Service.IsPathAllowedAsync(Project, dir.Path, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await stack.Service.IsPathAllowedAsync(Project, outside.Path, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    private sealed class MigratedGate : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
