using AiRaccoon.Core.Ingestion;
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
    public async Task AddAsync_SamePathTwice_IsANoOp_SingleWatch()
    {
        using var dir = TempDir.New("service-twice");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Store.Watches.Count.ShouldBe(1);
        (await stack.Service.StatusAsync(Project, TestContext.Current.CancellationToken)).Count.ShouldBe(1);
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
}
