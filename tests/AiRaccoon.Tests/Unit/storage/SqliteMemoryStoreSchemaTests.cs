using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Schema migration: opening a legacy bank (no source_file column,
///     single-column entries_fts) upgrades it to the weighted two-column index, and fresh
///     banks are created in the new shape directly.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreSchemaTests : IDisposable
{
    // The legacy schema shape: entries without source_file and a single-column entries_fts.
    private const string LegacyDdl = """
                                     CREATE TABLE IF NOT EXISTS entries (
                                         id INTEGER PRIMARY KEY,
                                         hash TEXT,
                                         path TEXT,
                                         value TEXT,
                                         scope TEXT CHECK(scope IN ('shared','project','custom')) NULL,
                                         project_id TEXT NULL,
                                         context_label TEXT NULL,
                                         workspace_id TEXT NULL,
                                         agent_id TEXT NULL,
                                         created_at INTEGER NOT NULL,
                                         updated_at INTEGER NOT NULL,
                                         access_count INTEGER NOT NULL DEFAULT 0,
                                         last_accessed_at INTEGER NULL,
                                         rating REAL NOT NULL DEFAULT 0.5,
                                         ttl_days INTEGER NULL,
                                         embed_state TEXT NOT NULL DEFAULT 'pending' CHECK(embed_state IN ('pending','embedded')),
                                         embedding BLOB NULL
                                     );

                                     CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                                         value,
                                         content='entries',
                                         content_rowid='id'
                                     );

                                     CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
                                         INSERT INTO entries_fts(rowid, value) VALUES (new.id, new.value);
                                     END;
                                     """;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-schema");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreSchemaTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task OpenBank_OnLegacySchema_AddsSourceFileColumn_AndRebuildsWeightedFts()
    {
        await CreateLegacyBankAsync();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var hasColumn = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pragma_table_info('entries') WHERE name = 'source_file'");
        hasColumn.ShouldBe(1, "the legacy bank must gain the source_file column on open");

        var hasSection = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pragma_table_info('entries') WHERE name = 'section'");
        hasSection.ShouldBe(1, "the legacy bank must gain the section column on open");

        var ftsSql = await connection.ExecuteScalarAsync<string?>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'");
        ftsSql.ShouldNotBeNull();
        ftsSql.ShouldContain("source_file");
        ftsSql.ShouldContain("section");
        ftsSql.ShouldContain("content='entries'");

        // The legacy row's source_file is NULL (unknown), so it must not break the search path.
        var rows = await connection.QueryAsync<dynamic>(
            "SELECT e.hash AS Hash, e.value AS Value, e.source_file AS SourceFile, " +
            "bm25(entries_fts, 1.0, 8.0) AS Ranking FROM entries_fts " +
            "JOIN entries e ON e.id = entries_fts.rowid WHERE entries_fts MATCH 'legacy'");
        rows.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task OpenBank_OnLegacySchema_NewWritesSyncSourceFileIntoFts()
    {
        await CreateLegacyBankAsync();

        var store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        await store.WriteAsync(new MemoryWriteRequest("acme", "fresh wave two content",
            SourceFile: "docs/adr/0001-legacy-migrated.md"), TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(new SearchQuery("acme", "wave two",
            SearchScope.Project, Limit: 5, MinScore: 0.0), TestContext.Current.CancellationToken);
        var hit = results.ShouldHaveSingleItem();
        hit.SourceFile.ShouldBe("docs/adr/0001-legacy-migrated.md");
        hit.TotalChunks.ShouldBe(1);
        hit.ChunkIndex.ShouldBe(0);
    }

    [Fact]
    public async Task OpenBank_OnFreshDatabase_CreatesWeightedFtsWithSourceFile()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var ftsSql = await connection.ExecuteScalarAsync<string?>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'");
        ftsSql.ShouldNotBeNull();
        ftsSql.ShouldContain("source_file");
        ftsSql.ShouldContain("section");

        var hasColumn = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pragma_table_info('entries') WHERE name = 'source_file'");
        hasColumn.ShouldBe(1);
    }

    [Fact]
    public async Task OpenBank_AfterFtsShellCrash_RecreatesAndRepopulatesIndex()
    {
        // A crash between the migration's DROP/CREATE and its repopulate leaves a
        // new-shape FTS table with no rows and no triggers — it must heal on reopen.
        var store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        await store.WriteAsync(new MemoryWriteRequest("acme", "shell crash recovery content"),
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                """
                DROP TRIGGER IF EXISTS entries_fts_ai;
                DROP TRIGGER IF EXISTS entries_fts_ad;
                DROP TRIGGER IF EXISTS entries_fts_au;
                DROP TABLE IF EXISTS entries_fts;
                CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section,
                    content='entries', content_rowid='id');
                """,
                TestContext.Current.CancellationToken);
        }

        await using var reopened = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var ftsRows = await reopened.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries_fts", TestContext.Current.CancellationToken);
        var entryRows = await reopened.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries", TestContext.Current.CancellationToken);
        ftsRows.ShouldBe(entryRows, "the empty shell must be detected and repopulated on reopen");
        ftsRows.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task OpenBank_CreatesUniqueBucketIndexes()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var names = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'uq_entries_%'",
                TestContext.Current.CancellationToken))
            .ToList();

        names.ShouldContain("uq_entries_shared_bucket");
        names.ShouldContain("uq_entries_committed_bucket");
    }

    [Fact]
    public async Task OpenBank_MigrationCollapsesSharedDuplicates_KeepingEarliestRow()
    {
        await SeedDuplicateBucketsAsync(("h1", "shared/a.md", "shared", "acme", null),
            ("h1", "shared/a.md", "shared", "beta", null));

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(long Id, string ProjectId)>(
                "SELECT id AS Id, project_id AS ProjectId FROM entries WHERE scope = 'shared'",
                TestContext.Current.CancellationToken))
            .ToList();
        rows.ShouldHaveSingleItem();
        rows[0].ProjectId.ShouldBe("acme"); // MIN(id) survivor keeps the earliest promoter
    }

    [Fact]
    public async Task OpenBank_MigrationCollapsesProjectDuplicates_WithNullContextLabel()
    {
        await SeedDuplicateBucketsAsync(("h1", "p1.md", "project", "acme", null),
            ("h1", "p1.md", "project", "acme", null));

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE scope = 'project'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task OpenBank_MigrationLeavesWorkspaceRowsUntouched()
    {
        await SeedDuplicateBucketsAsync(("w1", "ws.md", null, "acme", "ws-1"),
            ("w1", "ws.md", null, "acme", "ws-1"));

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE workspace_id IS NOT NULL",
            TestContext.Current.CancellationToken);
        count.ShouldBe(2); // workspace scope is unconstrained by design
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateBucketInsert()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('h1', 'shared/a.md', 'x', 'shared', 'acme', 1, 1)",
            TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SqliteException>(() => connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('h1', 'shared/a.md', 'x', 'shared', 'beta', 2, 2)",
            TestContext.Current.CancellationToken));

        ex.SqliteErrorCode.ShouldBe(19); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task UniqueIndex_AllowsSamePathDifferentHash_AndWorkspaceDuplicates()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        // Chunk rows: same path, different hash must stay legal.
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('h1', 'doc.md', 'chunk one', 'project', 'acme', 1, 1)",
            TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('h2', 'doc.md', 'chunk two', 'project', 'acme', 1, 1)",
            TestContext.Current.CancellationToken);

        // Workspace rows: same bucket key twice stays legal (scope NULL escapes the partials).
        await connection.ExecuteAsync(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', 'acme', 'Active', 1)",
            TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at) " +
            "VALUES ('w1', 'ws.md', 'x', NULL, 'acme', 'ws-1', 1, 1)",
            TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at) " +
            "VALUES ('w1', 'ws.md', 'x', NULL, 'acme', 'ws-1', 1, 1)",
            TestContext.Current.CancellationToken);

        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries", TestContext.Current.CancellationToken);
        count.ShouldBe(4);
    }

    [Fact]
    public async Task OpenBank_MigrationIsIdempotent_OnAlreadyIndexedBank()
    {
        await SeedDuplicateBucketsAsync(("h1", "shared/a.md", "shared", "acme", null),
            ("h1", "shared/a.md", "shared", "beta", null));

        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            // First open heals.
        }

        await using var reopened = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await reopened.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE scope = 'shared'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1, "the dedupe must not run again once the indexes exist");
    }

    /// <summary>
    ///     Opens a fresh bank, drops the unique bucket indexes (so duplicate rows can be
    ///     seeded), inserts the given raw rows, and closes — leaving a bank that the next
    ///     open must heal. Mirrors the real production bank shape: current DDL, no indexes.
    /// </summary>
    private async Task SeedDuplicateBucketsAsync(params (string Hash, string Path, string? Scope, string ProjectId, string? WorkspaceId)[] rows)
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "DROP INDEX IF EXISTS uq_entries_shared_bucket; DROP INDEX IF EXISTS uq_entries_committed_bucket;",
                TestContext.Current.CancellationToken);
            foreach (var (hash, path, scope, projectId, workspaceId) in rows)
            {
                if (workspaceId is not null)
                {
                    await connection.ExecuteAsync(
                        "INSERT OR IGNORE INTO workspaces (id, project_id, status, created_at) VALUES (@workspaceId, @projectId, 'Active', 1)",
                        new { workspaceId, projectId });
                }

                await connection.ExecuteAsync(
                    "INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at) " +
                    "VALUES (@hash, @path, 'dup', @scope, @projectId, @workspaceId, 1, 1)",
                    new { hash, path, scope, projectId, workspaceId });
            }
        }
    }

    private async Task CreateLegacyBankAsync()
    {
        var bankPath = Path.Combine(_dataRoot, "memory.db");
        Directory.CreateDirectory(_dataRoot);
        await using var connection = new SqliteConnection($"Data Source={bankPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LegacyDdl;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('h1', 'p1.md', 'legacy content row', 'project', 'acme', 0, 0)",
            TestContext.Current.CancellationToken);
    }
}
