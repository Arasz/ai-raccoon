using AiRaccoon.Core.Isolation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     FR-NM-1 (see docs/work/features-native-memory/native-memory.feature) acceptance: the bank is one self-describing SQLite file and all metadata lives
///     inside it — no raccoon_meta.db, entry metadata on the entry row, workspace lifecycle rows
///     in memory.db.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NativeMemoryFeatureTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;

    public NativeMemoryFeatureTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task BankDirectory_ContainsExactlyMemoryDb_NoRaccoonMetaDb()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
                "VALUES ('h1', 'note.md', 'a fact', 'project', 'acme', 1, 1)";
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // A workspace lifecycle write must also stay inside memory.db.
        await new SqliteWorkspaceStore(_factory).BeginAsync(
            new Workspace("ws-1", "acme"), new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        var dbFiles = Directory.EnumerateFiles(_dataRoot, "*.db").Select(Path.GetFileName).ToList();

        dbFiles.ShouldBe(["memory.db"]);
        File.Exists(Path.Combine(_dataRoot, "raccoon_meta.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task EntryMetadata_IsStoredOnTheEntryRow()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO entries (hash, path, value, scope, project_id, context_label, agent_id, created_at, updated_at, " +
            "access_count, last_accessed_at, rating, ttl_days, embed_state) " +
            "VALUES ('h1', 'note.md', 'a fact', 'custom', 'acme', 'docs:api', 'agent-1', 10, 20, 3, 30, 0.9, 15, 'pending')";
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var row = await connection.QueryFirstOrDefaultAsync<EntryRow>(
            """
            SELECT hash AS Hash, scope AS Scope, context_label AS ContextLabel, agent_id AS AgentId,
                   access_count AS AccessCount, last_accessed_at AS LastAccessedAt, rating AS Rating,
                   ttl_days AS TtlDays, embed_state AS EmbedState
            FROM entries
            """);

        row.ShouldNotBeNull();
        row.AgentId.ShouldBe("agent-1");
        row.AccessCount.ShouldBe(3);
        row.LastAccessedAt.ShouldBe(30);
        row.Rating.ShouldBe(0.9);
        row.TtlDays.ShouldBe(15);
        row.EmbedState.ShouldBe("pending");

        // The columns exist on the row, not in a sidecar table (FR-NM-1 s2; see docs/work/features-native-memory/native-memory.feature).
        var columns = (await connection.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('entries')"))
            .ToList();
        columns.ShouldContain("access_count");
        columns.ShouldContain("last_accessed_at");
        columns.ShouldContain("rating");
        columns.ShouldContain("ttl_days");
        columns.ShouldContain("agent_id");
    }

    [Fact]
    public async Task WorkspaceBegin_CreatesActiveRowInMemoryDb()
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await workspaceStore.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<WorkspaceRow>(
            """
            SELECT id AS Id, project_id AS ProjectId, status AS Status, created_at AS CreatedAt, closed_at AS ClosedAt
            FROM workspaces
            WHERE id = 'ws-1'
            """);

        row.ShouldNotBeNull();
        row.ProjectId.ShouldBe("acme");
        row.Status.ShouldBe(WorkspaceStatus.Active.ToString());
        row.CreatedAt.ShouldBe(startedAt.ToUnixTimeSeconds());
        row.ClosedAt.ShouldBeNull();
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot();

    private sealed class EntryRow
    {
        public string Hash { get; set; } = "";

        public string Scope { get; set; } = "";

        public string? ContextLabel { get; set; }

        public string AgentId { get; set; } = "";

        public int AccessCount { get; set; }

        public long? LastAccessedAt { get; set; }

        public double Rating { get; set; }

        public int? TtlDays { get; set; }

        public string EmbedState { get; set; } = "";
    }

    private sealed class WorkspaceRow
    {
        public string Id { get; set; } = "";

        public string ProjectId { get; set; } = "";

        public string Status { get; set; } = "";

        public long CreatedAt { get; set; }

        public long? ClosedAt { get; set; }
    }
}
