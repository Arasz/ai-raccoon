using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0089 decision 5: the <c>projects</c> registry table lives in the unconditional
///     <c>Ddl</c> block beside <c>metrics</c> — no <see cref="MemorySchema.CurrentVersion" />
///     bump, so it reaches a legacy bank via the digest-rerun path (ADR-0086), not a ladder step.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectsTableDdlTests
{
    [RetryFact]
    public async Task OpeningALegacyBank_CreatesTheProjectsTable()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, project_id, scope, created_at, updated_at)
            VALUES ('h1', 'p1', 'a memory row', 'acme', 'project', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));

        // Simulate a pre-6a bank: the projects table doesn't exist yet and the digest is stale —
        // the shape a v10 bank stamped before this task's Ddl change actually has.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS projects", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "projects")).ShouldBeTrue("a legacy bank must gain the projects table on reopen");
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM entries", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L, "existing memory rows must survive the projects table addition");
    }

    [RetryFact]
    public async Task CreatingTheProjectsTable_DoesNotChangeUserVersion()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // Pin the historical fact, not MemorySchema.CurrentVersion: reading both sides after the
        // SAME EnsureAsync call is tautological — a fresh bank's first open always stamps
        // CurrentVersion in one shot (the `fresh` branch), and the second call here never revisits
        // the ladder because storedVersion already equals CurrentVersion, so before == after holds
        // for any value of CurrentVersion and even with the projects table deleted from Ddl
        // entirely (both measured against this exact test, review round 1, PR #546). A real
        // current-version bank is stamped independently of what this binary's CurrentVersion
        // compiles to. (Originally pinned at literal 10; the v11 tombstone migration in PR #576
        // legitimately turned that red — a sub-current bank now climbs the ladder — so the pin
        // moved to CurrentVersion. The fact under test stands: the digest rerun that recreates
        // the projects table never moves user_version by itself.)
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {MemorySchema.CurrentVersion}", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE IF EXISTS projects", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "projects")).ShouldBeTrue("a legacy bank must gain the projects table on reopen");
        (await ReadUserVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion,
            "the projects table is additive Ddl, not a ladder step — the digest rerun that recreates it must not move user_version");
    }

    [RetryFact]
    public async Task FreshBank_HasTheProjectsTableWithTheDecidedShape()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = (await connection.QueryAsync<(string Name, string Type, long IsRequired, long Pk)>(new CommandDefinition(
                """SELECT name AS Name, type AS Type, "notnull" AS IsRequired, pk AS Pk FROM pragma_table_info('projects')""",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ToDictionary(c => c.Name, StringComparer.Ordinal);

        columns.Keys.ShouldBe(["id", "name", "created_at"], ignoreOrder: true);
        columns["id"].Type.ShouldBe("TEXT");
        columns["id"].Pk.ShouldBe(1L, "id must be the primary key — RegisterAsync's ON CONFLICT(id) depends on it");
        columns["name"].Type.ShouldBe("TEXT");
        columns["name"].IsRequired.ShouldBe(0L, "name is optional — never an identifier (ADR-0089 decision 5)");
        columns["created_at"].Type.ShouldBe("INTEGER");
        columns["created_at"].IsRequired.ShouldBe(1L, "created_at is required");
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;

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
}
