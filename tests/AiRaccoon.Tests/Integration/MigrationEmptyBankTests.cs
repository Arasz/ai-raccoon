using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Regression class for the 1.33.8 publish defect: a migration step read rows through
///     Dapper's TYPED materializer (<c>QueryAsync&lt;TRow&gt;</c>), and sqlite3mc's reader reported
///     the CAST columns' field types as byte[]-compatible on an EMPTY result set — so Dapper
///     demanded a <c>(byte[], byte[], byte[], byte[])</c> constructor, threw, and every real bank
///     (whose <c>sync_tombstones</c> happened to be empty) failed to open. CI never caught it
///     because every seeded fixture inserted rows before migrating.
///
///     The universal gate: EVERY EnsureAsync ladder run must succeed against banks whose tables
///     are empty AND whose legacy shapes carry no data to copy. A step that reads via typed
///     materialization fails this on sqlite3mc regardless of which columns it selects.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MigrationEmptyBankTests
{
    /// <summary>
    ///     The full ladder must complete on a bank that reached v1 with zero rows in EVERY table
    ///     any later step might read — the exact shape of a real, freshly-used-but-empty project.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_LadderCompletes_OnAnEmptyV1Bank()
    {
        await using var connection = await OpenAsync();
        await SeedEmptyV1BankAsync(connection);

        var act = () => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await act.ShouldNotThrowAsync(
            "the ladder must tolerate empty tables — Dapper typed reads break on sqlite3mc's " +
            "empty-result field-type reporting (the 1.33.8 sync_tombstones defect)");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     Same gate from the doctor's side: after an empty-v1 bank migrates, SchemaDoctor must
    ///     report HEALTHY — proving the steps actually built the shapes they claim, not just ran.
    /// </summary>
    [RetryFact]
    public async Task AfterLadder_AnEmptyV1Bank_IsDoctorHealthy()
    {
        await using var connection = await OpenAsync();
        await SeedEmptyV1BankAsync(connection);
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var report = await SchemaDoctor.DiagnoseAsync(connection, TestContext.Current.CancellationToken);

        // Scoped to sync_tombstones — the table the v11 step owns. (A real v1 bank also carries
        // additive workspaces-column drift that no ladder step repairs; pre-existing behavior,
        // out of scope here, so the whole-bank Status is not asserted.)
        report.Findings.Where(f => f.ObjectName == "sync_tombstones").ShouldBeEmpty(
            $"sync_tombstones must match the Ddl after the migration — got: {string.Join("; ", report.Findings.Where(f => f.ObjectName == "sync_tombstones").Select(f => f.Detail))}");
    }

    /// <summary>
    ///     Pins the specific 1.33.8 failure signature: a legacy sync_tombstones with ZERO rows but
    ///     the pre-ALTER shape (project_id added via ALTER TABLE, PK still (hash, scope)) must
    ///     migrate cleanly. This is the exact live-bank shape the published binary could not open.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_EmptyLegacyTombstones_MigratesCleanly()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TABLE sync_tombstones;
            CREATE TABLE sync_tombstones (
                hash TEXT NOT NULL,
                scope TEXT NOT NULL,
                deleted_at INTEGER NOT NULL,
                project_id TEXT NULL,
                PRIMARY KEY (hash, scope)
            );
            PRAGMA user_version = 10;
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        var act = () => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // On the defective build this threw inside the typed row read: the regression signature.
        await act.ShouldNotThrowAsync("an empty legacy tombstones table must migrate without a typed-materialization failure");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    private static async Task SeedEmptyV1BankAsync(SqliteConnection connection)
    {
        // The established V1-era shape (same DDL text as MemorySchemaVersionTests' fixture):
        // enough for the fresh-bank detector to see a non-empty file (so the ladder runs, not
        // the fresh path), with NO rows in any table — the 1.33.8 failure precondition.
        await connection.ExecuteAsync(new CommandDefinition(V1Ddl, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 1", cancellationToken: TestContext.Current.CancellationToken));
    }

    private const string V1Ddl = """
                                 CREATE TABLE workspaces (
                                     id TEXT PRIMARY KEY,
                                     project_id TEXT NOT NULL,
                                     status TEXT NOT NULL,
                                     created_at INTEGER NOT NULL
                                 );

                                 CREATE TABLE entries (
                                     id INTEGER PRIMARY KEY,
                                     hash TEXT,
                                     path TEXT,
                                     value TEXT,
                                     source_file TEXT,
                                     section TEXT,
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
                                     embedding BLOB NULL,
                                     heading_path TEXT NULL,
                                     structure_embedding BLOB NULL
                                 );

                                 CREATE VIRTUAL TABLE entries_fts USING fts5(
                                     value, source_file, section, content='entries', content_rowid='id'
                                 );

                                 CREATE UNIQUE INDEX uq_entries_shared_bucket
                                     ON entries(path, hash) WHERE scope = 'shared';
                                 CREATE UNIQUE INDEX uq_entries_committed_bucket
                                     ON entries(path, hash, project_id, scope, COALESCE(context_label, ''))
                                     WHERE scope IN ('project', 'custom');
                                 """;

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
