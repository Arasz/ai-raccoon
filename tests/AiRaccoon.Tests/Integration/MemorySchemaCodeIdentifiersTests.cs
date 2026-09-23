using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     P2-B (docs/work/2026-09-23-code-retrieval-eval-plan.md §P2, docs/adr/0108): the digest-gated,
///     no-ladder-step migration that adds <c>code_entries.identifiers</c> and rebuilds
///     <c>code_fts</c> to a 3-column shape on a bank stamped before this task's Ddl change.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaCodeIdentifiersTests
{
    [RetryFact]
    public async Task OpenBank_TwoColumnCodeFts_RebuildsWithIdentifiersAndBackfills()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        const string value = "sealed class WatchOverlapResolver { }";
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end,
                                       project_id, created_at, updated_at)
            VALUES (1, 'h1', 'src/Watch/WatchOverlapResolver.cs', @value,
                    'src/Watch/WatchOverlapResolver.cs', 1, 1, 'acme', 1, 1)
            """, new { value }, cancellationToken: TestContext.Current.CancellationToken));

        // Revert code_fts to the pre-P2-B 2-column shape a bank stamped before this task's Ddl
        // change actually has (MemorySchemaCodeCorpusTests' own precedent: drop and recreate only
        // the affected objects, then roll back the digest).
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TRIGGER code_fts_ai;
            DROP TRIGGER code_fts_ad;
            DROP TRIGGER code_fts_au;
            DROP TABLE code_fts;

            CREATE VIRTUAL TABLE code_fts USING fts5(value, source_file, content='code_entries', content_rowid='id');

            CREATE TRIGGER code_fts_ai AFTER INSERT ON code_entries BEGIN
                INSERT INTO code_fts(rowid, value, source_file) VALUES (new.id, new.value, new.source_file);
            END;
            CREATE TRIGGER code_fts_ad AFTER DELETE ON code_entries BEGIN
                INSERT INTO code_fts(code_fts, rowid, value, source_file) VALUES ('delete', old.id, old.value, old.source_file);
            END;
            CREATE TRIGGER code_fts_au AFTER UPDATE OF value, source_file ON code_entries BEGIN
                INSERT INTO code_fts(code_fts, rowid, value, source_file) VALUES ('delete', old.id, old.value, old.source_file);
                INSERT INTO code_fts(rowid, value, source_file) VALUES (new.id, new.value, new.source_file);
            END;

            INSERT INTO code_fts(rowid, value, source_file) SELECT id, value, source_file FROM code_entries;
            """, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var ftsSql = await SqlOfAsync(connection, "code_fts");
        ftsSql.ShouldNotBeNull();
        ftsSql.ShouldContain("identifiers");

        var identifiers = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT identifiers FROM code_entries WHERE id = 1", cancellationToken: TestContext.Current.CancellationToken));
        identifiers.ShouldBe(IdentifierSplitter.Identifiers(value));

        var hit = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM code_fts WHERE code_fts MATCH 'overlap'",
            cancellationToken: TestContext.Current.CancellationToken));
        hit.ShouldBe(1L, "the rebuilt index must be populated from the backfilled identifiers, not left empty");
    }

    /// <summary>A bank already on the 3-column shape must not be rebuilt again on a later digest-mismatch open.</summary>
    [RetryFact]
    public async Task OpenBank_AlreadyMigrated_IsNoOp()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end,
                                       project_id, created_at, updated_at, identifiers)
            VALUES (1, 'h1', 'p1.cs', 'class Foo', 'p1.cs', 1, 1, 'acme', 1, 1, 'sentinel value')
            """, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A no-op migration must not have rebuilt code_fts, which would recompute identifiers from
        // value ("class Foo" splits into no multi-part token, so a rebuild would overwrite the
        // sentinel with "") — a directly falsifiable proof the skip check actually short-circuits.
        var identifiers = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT identifiers FROM code_entries WHERE id = 1", cancellationToken: TestContext.Current.CancellationToken));
        identifiers.ShouldBe("sentinel value");
    }

    /// <summary>code_fts_au fires AFTER UPDATE OF value, source_file, identifiers — an identifiers-only reindex is not stale.</summary>
    [RetryFact]
    public async Task UpdateValue_ReindexesIdentifiers()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end,
                                       project_id, created_at, updated_at, identifiers)
            VALUES (1, 'h1', 'p1.cs', 'class Foo', 'p1.cs', 1, 1, 'acme', 1, 1, '')
            """, cancellationToken: TestContext.Current.CancellationToken));

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM code_fts WHERE code_fts MATCH 'overlap'",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(0L);

        const string newValue = "sealed class WatchOverlapResolver { }";
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET value = @value, identifiers = @identifiers WHERE id = 1",
            new { value = newValue, identifiers = IdentifierSplitter.Identifiers(newValue) },
            cancellationToken: TestContext.Current.CancellationToken));

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM code_fts WHERE code_fts MATCH 'overlap'",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L);
    }

    private static async Task<string?> SqlOfAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken));

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
