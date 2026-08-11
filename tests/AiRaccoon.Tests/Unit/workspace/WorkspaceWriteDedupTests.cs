using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.workspace;

/// <summary>
///     H3: SqliteMemoryStore.WriteAsync's committed-value dedup lookup ran before the workspace
///     branch, so a memory_write naming a workspace could be silently swallowed by an
///     already-committed row holding identical content — the write landed in project scope
///     instead, and memory_workspace_discard had nothing to take back.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WorkspaceWriteDedupTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-workspace-dedup");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public WorkspaceWriteDedupTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new StubChunker(),
            new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task Write_ToWorkspace_WhenContentAlreadyCommitted_StillLandsInTheWorkspace()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "already committed content"),
            TestContext.Current.CancellationToken);
        await EnsureWorkspaceAsync("ws-1");

        var result = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "already committed content", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        result.Context.ShouldBe("workspace:ws-1",
            "the caller named a workspace; a pre-existing committed row must not redirect the write");
        var status = await _store.ListContextAsync(ProjectId, ContextNaming.WorkspaceContext("ws-1"),
            TestContext.Current.CancellationToken);
        status.Count.ShouldBe(1, "memory_workspace_status must report the entry the caller just wrote");

        var rowCount = await CountRowsAsync(result.Hash);
        rowCount.ShouldBe(2, "both the pre-existing committed row and the new workspace row must exist");
    }

    [Fact]
    public async Task Write_ToWorkspace_WhenContentAlreadyCommitted_TheNewRowCarriesTheWorkspaceId()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "duplicate note"),
            TestContext.Current.CancellationToken);
        await EnsureWorkspaceAsync("ws-1");

        var result = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "duplicate note", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var workspaceRowExists = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE hash = @hash AND workspace_id = @workspaceId",
            new { hash = result.Hash, workspaceId = "ws-1" });
        workspaceRowExists.ShouldBe(1L);
    }

    [Fact]
    public async Task Discard_AfterAWorkspaceWriteSwallowedByDedup_RemovesOnlyTheWorkspaceRow()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "keep me committed"),
            TestContext.Current.CancellationToken);
        await EnsureWorkspaceAsync("ws-1");
        var result = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "keep me committed", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var discarded = await _store.DeleteContextAsync(ProjectId, ContextNaming.WorkspaceContext("ws-1"),
            TestContext.Current.CancellationToken);

        discarded.ShouldBe(1, "memory_workspace_discard must find exactly the workspace's own row to take back");
        var rowCount = await CountRowsAsync(result.Hash);
        rowCount.ShouldBe(1, "the committed project row must survive the workspace discard");
        var committedStats = await _store.GetStatsAsync(ProjectId, TestContext.Current.CancellationToken);
        committedStats.EntryCount.ShouldBe(1);
    }

    /// <summary>The genuine dedup case must keep working: two identical committed writes still
    /// collapse into one row.</summary>
    [Fact]
    public async Task Write_TwiceToCommittedScope_WithIdenticalContent_StillProducesOneRow()
    {
        var first = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "committed twice"),
            TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "committed twice"),
            TestContext.Current.CancellationToken);

        second.Hash.ShouldBe(first.Hash);
        (await CountRowsAsync(first.Hash)).ShouldBe(1);
    }

    /// <summary>Reverse ordering (brief's regression case): a workspace write followed by a
    /// committed write of the same content must still produce two rows.</summary>
    [Fact]
    public async Task Write_ToWorkspaceThenToCommittedScope_WithIdenticalContent_ProducesTwoRows()
    {
        await EnsureWorkspaceAsync("ws-1");
        var workspaceEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "workspace first", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var projectEntry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "workspace first"),
            TestContext.Current.CancellationToken);

        projectEntry.Hash.ShouldBe(workspaceEntry.Hash);
        projectEntry.Context.ShouldBe("project:acme");
        (await CountRowsAsync(projectEntry.Hash)).ShouldBe(2);
    }

    private async Task EnsureWorkspaceAsync(string workspaceId)
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync(new Workspace(workspaceId, ProjectId), FixedNow,
            TestContext.Current.CancellationToken);
    }

    private async Task<long> CountRowsAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE hash = @hash", new { hash });
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
