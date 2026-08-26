using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP4 / plan D3: the dimension reconcile is phase 1 of the migration drain — after the lease is
///     taken and before the first pending row is selected. Ordering is the whole point: reconcile
///     after the loop and the DROP wipes everything the drain just wrote, with no pending rows left
///     to re-drive it, so the migration finishes green over empty vec tables.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DrainReconcilesDimensionsFirstTests
{
    [RetryFact]
    public async Task DrainMigrationAsync_ReconcilesBeforeEmbeddingTheFirstRow()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await SeedPendingEntryAsync(connection);
        await ConfigureLocalEngineAsync(connection);
        await OpenMigrationAsync(connection);

        var reconciler = new RecordingReconciler(connection);
        var embedder = TestData.CreateEntryEmbedder(new CountingEmbeddingService(), new SqliteModelMigrationLease(TimeProvider.System),
            new FakeTimeProvider(), reconciler);

        await embedder.DrainMigrationAsync(connection, Ct);

        reconciler.Calls.ShouldBe(1, "the drain reconciles exactly once, not per batch");
        reconciler.EmbeddedRowsWhenCalled.ShouldBe(0,
            "reconcile must run before the first row is embedded — after the loop, the DROP discards them");
    }

    /// <summary>A vec table dropped at runtime is not healed by the next open (the Ddl block is
    /// digest-gated, ADR-0075), so the drain is the only thing that can bring it back.</summary>
    [RetryFact]
    public async Task DrainMigrationAsync_WithVecEntriesDropped_RecreatesItAndFillsIt()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await SeedPendingEntryAsync(connection);
        await ConfigureLocalEngineAsync(connection);
        await OpenMigrationAsync(connection);
        await connection.ExecuteAsync(new CommandDefinition("DROP TABLE vec_entries", cancellationToken: Ct));

        var embedder = TestData.CreateEntryEmbedder(new CountingEmbeddingService(), new SqliteModelMigrationLease(TimeProvider.System),
            new FakeTimeProvider(), new VecDimensionReconciler());

        await embedder.DrainMigrationAsync(connection, Ct);

        var sql = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_entries'",
            cancellationToken: Ct));
        sql.ShouldNotBeNull("the drain recreates a vec table that no longer exists");
        sql.ShouldContain("float[384]");
        (await CountAsync(connection, "SELECT count(*) FROM vec_entries")).ShouldBe(1,
            "the drain's MarkEmbedded refills the recreated table through the triggers");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingReconciler(SqliteConnection connection) : IVecDimensionReconciler
    {
        public int Calls { get; private set; }

        public long EmbeddedRowsWhenCalled { get; private set; } = -1;

        public async Task<bool> ReconcileAsync(SqliteConnection _, SqliteTransaction? transaction,
            int targetDimension, IReadOnlyCollection<string> tables, CancellationToken cancellationToken)
        {
            Calls++;
            EmbeddedRowsWhenCalled = await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE embed_state = 'embedded'");
            return false;
        }
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: Ct));

    private static async Task SeedPendingEntryAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries(hash, value, project_id, scope, created_at, updated_at, embed_state)
            VALUES ('h1', 'a value to embed', 'p', 'project', 0, 0, 'pending')
            """, cancellationToken: Ct));

    /// <summary>The drain reconciles against the configured engine; a bank with none is left alone.</summary>
    private static async Task ConfigureLocalEngineAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES ('embedding.provider', 'local') " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value", cancellationToken: Ct));

    private static async Task OpenMigrationAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO model_migration(id, provider, model, base_url, engine, started_at, finished_at)
            VALUES (1, 'local', NULL, NULL, 'test-engine', 0, NULL)
            ON CONFLICT(id) DO UPDATE SET finished_at = NULL, started_at = 0
            """, cancellationToken: Ct));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
