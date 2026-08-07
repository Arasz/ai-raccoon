using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Watch;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     memory_watch_add/status/remove behavior over a fake IWatchService: param validation,
///     error-code mapping, result shapes, and the no-op idempotency contracts.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchToolsTests
{
    private readonly FakeWatchService _watch = new();
    private readonly WatchTools _tools;

    public WatchToolsTests()
    {
        _tools = new WatchTools(_watch, new ToolGate(new FakeAccessGuard(), new FakePromotionQueue()),
            new ToolCallMetrics());
    }

    [Fact]
    public async Task Add_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Add("", "/repo", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("invalid-params");
        _watch.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_DelegatesToService_AndReturnsResult()
    {
        var result = await _tools.Add("proj-a", "/repo", TestContext.Current.CancellationToken);

        _watch.Added.ShouldBe([("proj-a", "/repo")]);
        result.Data!.ProjectId.ShouldBe("proj-a");
        result.Data!.Path.ShouldBe("/repo");
    }

    [Fact]
    public async Task Add_AlreadyWatched_IsNoOpAndDoesNotError()
    {
        var first = await _tools.Add("proj-a", "/repo", TestContext.Current.CancellationToken);
        var second = await _tools.Add("proj-a", "/repo", TestContext.Current.CancellationToken);

        _watch.Added.ShouldBe([("proj-a", "/repo"), ("proj-a", "/repo")]);
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Add_WatchingDisabled_MapsToWatchingDisabled()
    {
        _watch.AddError = new WatchDisabledException("proj-a");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Add("proj-a", "/repo", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("watching-disabled");
    }

    [Fact]
    public async Task Add_PathOutsideScope_MapsToPathOutsideScope()
    {
        _watch.AddError = new PathOutsideScopeException("/other");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Add("proj-a", "/other", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("path-outside-scope");
    }

    [Fact]
    public async Task Add_PathNotFound_MapsToPathNotFound()
    {
        _watch.AddError = new PathNotFound("/missing");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Add("proj-a", "/missing", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("path-not-found");
    }

    [Fact]
    public async Task Status_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Status("", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("invalid-params");
    }

    [Fact]
    public async Task Status_ReturnsWatchStatesWithErrorAndLastSync()
    {
        var lastSync = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        _watch.Status =
        [
            new WatchStatus("proj-a", "/repo", WatchState.Retrying, "boom", lastSync),
            new WatchStatus("proj-a", "/repo/docs", WatchState.Healthy)
        ];

        var result = await _tools.Status("proj-a", TestContext.Current.CancellationToken);

        result.Data!.Watches.Count.ShouldBe(2);
        result.Data!.Watches[0].State.ShouldBe(WatchState.Retrying);
        result.Data!.Watches[0].LastError.ShouldBe("boom");
        result.Data!.Watches[0].LastSync.ShouldBe(lastSync);
        result.Data!.Watches[1].State.ShouldBe(WatchState.Healthy);
    }

    [Fact]
    public async Task Status_EmptyWhenNoWatches()
    {
        var result = await _tools.Status("proj-a", TestContext.Current.CancellationToken);

        result.Data!.Watches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Remove("", "/repo", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("invalid-params");
        _watch.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_DelegatesToService_AndReturnsResult()
    {
        var result = await _tools.Remove("proj-a", "/repo", TestContext.Current.CancellationToken);

        _watch.Removed.ShouldBe([("proj-a", "/repo")]);
        result.Data!.ProjectId.ShouldBe("proj-a");
        result.Data!.Path.ShouldBe("/repo");
    }

    [Fact]
    public async Task Remove_NonExistentWatch_IsNoOpAndDoesNotError()
    {
        var result = await _tools.Remove("proj-a", "/never-registered", TestContext.Current.CancellationToken);

        _watch.Removed.ShouldBe([("proj-a", "/never-registered")]);
        result.Data!.ShouldNotBeNull();
    }

    private sealed class FakeWatchService : IWatchService
    {
        public List<(string ProjectId, string Path)> Added { get; } = [];

        public List<(string ProjectId, string Path)> Removed { get; } = [];

        public IReadOnlyList<WatchStatus> Status { get; set; } = [];

        public Exception? AddError { get; set; }

        public Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default)
        {
            if (AddError is not null)
            {
                throw AddError;
            }

            Added.Add((projectId, path));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default)
        {
            Removed.Add((projectId, path));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsPathAllowedAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeAccessGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Rw);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
