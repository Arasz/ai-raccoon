using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WI-5 / ADR-0011: the bank carries its schema shape in `PRAGMA user_version` instead of
///     re-deriving it from a column-and-index probe on every open.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaVersionTests
{
    [Fact]
    public async Task EnsureAsync_OnAFreshBank_StampsTheCurrentVersion()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    [Fact]
    public async Task EnsureAsync_OnAnUnstampedBank_RunsTheLadder_ThenStamps()
    {
        await using var connection = await OpenAsync();
        // A pre-versioning bank: the entries table exists in its oldest shape, and nothing
        // records which migrations it has seen.
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE entries (
                id INTEGER PRIMARY KEY,
                hash TEXT,
                path TEXT,
                value TEXT,
                scope TEXT NULL,
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
                embed_state TEXT NOT NULL DEFAULT 'pending',
                embedding BLOB NULL
            );
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync(connection, "entries");
        columns.ShouldContain("source_file");
        columns.ShouldContain("structure_embedding");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     The point of the marker: a stamped bank skips the ladder. Observed through the
    ///     bucket index, which only the ladder creates on an existing bank.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_OnAStampedBank_SkipsTheLadder()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP INDEX uq_entries_shared_bucket",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await IndexExistsAsync(connection, "uq_entries_shared_bucket"))
            .ShouldBeFalse("a stamped bank must not pay for the ladder's probes again");
    }

    /// <summary>The same witness from the other side: an unstamped bank does run the step.</summary>
    [Fact]
    public async Task EnsureAsync_OnAnUnstampedBank_MovesTheLegacyIngestScopeKeys()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO settings(key, value) VALUES ('watch.scope.acme', '[]');
            PRAGMA user_version = 0;
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await SettingExistsAsync(connection, "watch.scope.acme")).ShouldBeFalse();
        (await SettingExistsAsync(connection, "ingest.scope.acme")).ShouldBeTrue();
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // The DDL declares vec0 virtual tables, so the module has to be loaded exactly as
        // SqliteConnectionFactory.InitializeAsync does before the schema can be applied.
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<IReadOnlyCollection<string>> ColumnsAsync(SqliteConnection connection, string table) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT name FROM pragma_table_info('{table}')",
            cancellationToken: TestContext.Current.CancellationToken))).ToList();

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;

    private static async Task<bool> SettingExistsAsync(SqliteConnection connection, string key) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM settings WHERE key = @key",
            new { key }, cancellationToken: TestContext.Current.CancellationToken)) is not null;
}
