using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     H2: hash does not encode scope (ContentHash.Of(path, value)), so (project_id, hash) is not
///     a unique row when identical content lives in more than one scope. memory_set_ttl must stamp
///     only the project-scope row it was asked about, never a sibling sharing the same hash.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemoryScopeSiblingTtlTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-scope-sibling-ttl");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public MemoryScopeSiblingTtlTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task SetEntryTtl_WithWorkspaceSibling_LeavesTheWorkspaceRowsTtlNull()
    {
        await EnsureWorkspaceAsync("ws-1");
        // Workspace first, project second: the workspace row's workspace_id excludes it from the
        // committed-value dedup, so the project write still lands as its own row regardless of H3.
        var workspaceEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "the same note", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);
        var projectEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "the same note"),
            TestContext.Current.CancellationToken);
        projectEntry.Hash.ShouldBe(workspaceEntry.Hash,
            "the fixture this test needs: identical content, two scopes, one hash");

        var affected = await _store.SetEntryTtlAsync(ProjectId, projectEntry.Hash, 7,
            TestContext.Current.CancellationToken);

        affected.ShouldBeTrue();
        var rows = await ReadRowsAsync(projectEntry.Hash);
        rows.Count.ShouldBe(2);
        var projectRow = rows.Single(r => r.Scope == "project");
        projectRow.TtlDays.ShouldBe(7);
        var workspaceRow = rows.Single(r => r.WorkspaceId == "ws-1");
        workspaceRow.TtlDays.ShouldBeNull("a TTL set on the project hash must not stamp the workspace sibling");
    }

    [Fact]
    public async Task SetEntryTtl_WithCustomContextSibling_LeavesTheCustomRowsTtlNull()
    {
        var projectEntry = await _store.WriteAsync(
            new MemoryWriteRequest(ProjectId, "the same doc note"),
            TestContext.Current.CancellationToken);
        // memory_add_content skips the value dedup (WorkspaceService.ConsolidateAsync relies on
        // the same trait to promote into a fresh bucket) — reusing the project row's own
        // content-derived path builds the same-hash sibling this test needs.
        var customEntry = await _store.AddContentAsync(
            ProjectId, projectEntry.Path, "the same doc note", "docs:api",
            cancellationToken: TestContext.Current.CancellationToken);
        customEntry.Entry.Hash.ShouldBe(projectEntry.Hash,
            "the fixture this test needs: identical content, two scopes, one hash");

        var affected = await _store.SetEntryTtlAsync(ProjectId, projectEntry.Hash, 7,
            TestContext.Current.CancellationToken);

        affected.ShouldBeTrue();
        var rows = await ReadRowsAsync(projectEntry.Hash);
        rows.Count.ShouldBe(2);
        var projectRow = rows.Single(r => r.Scope == "project");
        projectRow.TtlDays.ShouldBe(7);
        var customRow = rows.Single(r => r.Scope == "custom");
        customRow.TtlDays.ShouldBeNull("a TTL set on the project hash must not stamp the custom-context sibling");
    }

    private async Task EnsureWorkspaceAsync(string workspaceId)
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync(new Workspace(workspaceId, ProjectId), FixedNow,
            TestContext.Current.CancellationToken);
    }

    private async Task<List<SiblingRow>> ReadRowsAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<SiblingRow>(
            """
            SELECT id AS Id, hash AS Hash, scope AS Scope, workspace_id AS WorkspaceId, ttl_days AS TtlDays
            FROM entries
            WHERE hash = @hash
            ORDER BY id
            """,
            new { hash });
        return [.. rows];
    }

    private sealed class SiblingRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";

        public string? Scope { get; set; }

        public string? WorkspaceId { get; set; }

        public int? TtlDays { get; set; }
    }

    private sealed class StubChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
