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

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Wave 2 schema migration: opening a pre-Wave-2 bank (no source_file column,
///     single-column entries_fts) upgrades it to the weighted two-column index, and fresh
///     banks are created in the new shape directly.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreSchemaTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-schema");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStoreSchemaTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    // The Wave 0 schema shape: entries without source_file and a single-column entries_fts.
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
            new TokenizerChunker(), new EmbeddingService());
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
