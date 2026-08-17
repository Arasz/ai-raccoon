using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Isolation;

/// <summary>
///     DA-F1 (WP5b, HIGH): <c>uq_entries_shared_bucket</c>/<c>uq_entries_committed_bucket</c> are
///     declared <c>WHERE scope = 'shared'</c> / <c>WHERE scope IN ('project','custom')</c>, but a
///     workspace row's CHECK constraint forces <c>scope IS NULL</c> — neither partial index can
///     ever match a workspace row, so <see cref="MemorySql.InsertEntry" />'s bare
///     <c>ON CONFLICT DO NOTHING</c> has nothing to conflict against for a workspace write, and a
///     retried write silently duplicates. These tests exercise the insert SQL directly (the exact
///     statement <c>SqliteMemoryStore.WriteAsync</c> runs) against the schema this worktree owns,
///     without depending on <c>SqliteMemoryStore.cs</c> itself.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WorkspaceEntryUniquenessTests : IDisposable
{
    private const string ProjectId = "acme";
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-workspace-uniqueness");
    private readonly SqliteConnectionFactory _factory;

    public WorkspaceEntryUniquenessTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task SequentialInsert_OfIdenticalWorkspaceContent_ProducesOneRow()
    {
        await SeedWorkspaceAsync("ws-1");

        await InsertEntryAsync("note.md", "h1", "ws-1", now: 1);
        await InsertEntryAsync("note.md", "h1", "ws-1", now: 2);

        (await CountEntriesAsync("h1", "ws-1")).ShouldBe(1L,
            "a retried write into a workspace must collapse into one row, exactly like the shared and committed buckets already do");
    }

    /// <summary>The RED case named by the acceptance criteria: two writes racing concurrently, not
    /// just sequentially, must still collapse to one row — proving the uniqueness comes from a real
    /// database constraint, not an application-level ordering assumption.</summary>
    [Fact]
    public async Task ConcurrentInsert_OfIdenticalWorkspaceContent_ProducesOneRow()
    {
        await SeedWorkspaceAsync("ws-1");

        await Task.WhenAll(
            InsertEntryAsync("note.md", "h1", "ws-1", now: 1),
            InsertEntryAsync("note.md", "h1", "ws-1", now: 1));

        (await CountEntriesAsync("h1", "ws-1")).ShouldBe(1L);
    }

    /// <summary>The genuine non-duplicate case must still work: different content, or content in a
    /// different workspace, must not be collapsed together.</summary>
    [Fact]
    public async Task Insert_OfDifferentWorkspaces_WithTheSamePathAndHash_KeepsBothRows()
    {
        await SeedWorkspaceAsync("ws-1");
        await SeedWorkspaceAsync("ws-2");

        await InsertEntryAsync("note.md", "h1", "ws-1", now: 1);
        await InsertEntryAsync("note.md", "h1", "ws-2", now: 1);

        (await CountEntriesAsync("h1", "ws-1")).ShouldBe(1L);
        (await CountEntriesAsync("h1", "ws-2")).ShouldBe(1L);
    }

    private async Task SeedWorkspaceAsync(string workspaceId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, @projectId, 'Active', 1)",
            new { id = workspaceId, projectId = ProjectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Runs the exact statement SqliteMemoryStore.WriteAsync uses for its insert step
    /// (MemorySql.InsertEntry) on its own connection, mirroring a retried memory_write.</summary>
    private async Task InsertEntryAsync(string path, string hash, string workspaceId, long now)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.InsertEntry,
            new
            {
                hash,
                path,
                value = "workspace content",
                sourceFile = (string?)null,
                section = (string?)null,
                scope = (string?)null,
                projectId = ProjectId,
                contextLabel = (string?)null,
                workspaceId,
                agentId = (string?)null,
                createdAt = now,
                updatedAt = now,
                sourceId = (long?)null,
                chunkIndex = -1,
                totalChunks = 0
            },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> CountEntriesAsync(string hash, string workspaceId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE hash = @hash AND workspace_id = @workspaceId",
            new { hash, workspaceId },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
