using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.storage;

/// <summary>
///     H2, sweep half: a project-scoped sweep pass must delete only the project-scope row it
///     enumerated, never a sibling row sharing the same hash in another scope — hash does not
///     encode scope (ContentHash.Of(path, value)), so (project_id, hash) is not a unique row.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SweepScopeSiblingTests : IDisposable
{
    private const string ProjectId = "acme";

    // Above the 0.5 default rating, so a TTL'd-but-otherwise-fresh entry can actually degrade —
    // mirrors the pattern in SetTtlToolTests.
    private const double Threshold = 0.9;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-sweep-sibling");
    private readonly SqliteConnectionFactory _factory;
    private readonly SweepService _sweeper;
    private readonly SqliteMemoryStore _store;

    public SweepScopeSiblingTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(),
            NullLogger<SqliteMemoryStore>.Instance);
        _sweeper = new SweepService(_store, _clock);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task Sweep_WithWorkspaceSibling_DeletesOnlyTheProjectRow()
    {
        await EnsureWorkspaceAsync("ws-1");
        var workspaceEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "sweepable shared note", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);
        var projectEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "sweepable shared note"),
            TestContext.Current.CancellationToken);
        projectEntry.Hash.ShouldBe(workspaceEntry.Hash,
            "the fixture this test needs: identical content, two scopes, one hash");
        await _store.SetEntryTtlAsync(ProjectId, projectEntry.Hash, 7, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(30));

        var outcome = await _sweeper.SweepAsync(ProjectId, Threshold, dryRun: false,
            TestContext.Current.CancellationToken);

        outcome.DeletedHashes.ShouldBe([projectEntry.Hash]);
        var rows = await ReadRowsAsync(projectEntry.Hash);
        rows.ShouldNotContain(r => r.Scope == "project", "the swept project row must be gone");
        var survivor = rows.ShouldHaveSingleItem();
        survivor.WorkspaceId.ShouldBe("ws-1",
            "the sibling in another scope must survive a sweep pass over the project context");

        var stillActive = await _store.ListContextAsync(ProjectId, ContextNaming.WorkspaceContext("ws-1"),
            TestContext.Current.CancellationToken);
        stillActive.Count.ShouldBe(1, "memory_workspace_status must still report the untouched workspace entry");
    }

    [Fact]
    public async Task Sweep_WithCustomContextSibling_DeletesOnlyTheProjectRow()
    {
        var projectEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "sweepable doc note"),
            TestContext.Current.CancellationToken);
        var customEntry = await _store.AddContentAsync(
            ProjectId, projectEntry.Path, "sweepable doc note", "docs:api",
            cancellationToken: TestContext.Current.CancellationToken);
        customEntry.Entry.Hash.ShouldBe(projectEntry.Hash,
            "the fixture this test needs: identical content, two scopes, one hash");
        await _store.SetEntryTtlAsync(ProjectId, projectEntry.Hash, 7, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(30));

        var outcome = await _sweeper.SweepAsync(ProjectId, Threshold, dryRun: false,
            TestContext.Current.CancellationToken);

        outcome.DeletedHashes.ShouldBe([projectEntry.Hash]);
        var rows = await ReadRowsAsync(projectEntry.Hash);
        var survivor = rows.ShouldHaveSingleItem();
        survivor.Scope.ShouldBe("custom",
            "the custom-context sibling must survive a sweep pass over the project context");
    }

    [Fact]
    public async Task Sweep_ProjectRowWithNoSibling_IsStillDeleted()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "sweepable lone note"), TestContext.Current.CancellationToken);
        await _store.SetEntryTtlAsync(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(30));

        var outcome = await _sweeper.SweepAsync(ProjectId, Threshold, dryRun: false,
            TestContext.Current.CancellationToken);

        outcome.DeletedHashes.ShouldBe([entry.Hash], "the scoped delete must not become inert for the ordinary case");
        (await ReadRowsAsync(entry.Hash)).ShouldBeEmpty();
    }

    private async Task EnsureWorkspaceAsync(string workspaceId)
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync(new Workspace(workspaceId, ProjectId), _clock.GetUtcNow(),
            TestContext.Current.CancellationToken);
    }

    private async Task<List<SiblingRow>> ReadRowsAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<SiblingRow>(
            """
            SELECT id AS Id, hash AS Hash, scope AS Scope, workspace_id AS WorkspaceId
            FROM entries
            WHERE hash = @hash
            ORDER BY id
            """,
            new { hash });
        return rows.ToList();
    }

    private sealed class SiblingRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";

        public string? Scope { get; set; }

        public string? WorkspaceId { get; set; }
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
