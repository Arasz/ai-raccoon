using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Each <c>BatchSize</c>-sized sub-batch marks its rows inside one <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c>,
///     so a failure mid-batch costs at most that batch. A real trigger aborts the third row's
///     UPDATE inside the SECOND batch: the first batch stays committed, the second rolls back
///     cleanly (an open transaction would make the one-row retries fail to begin), and the
///     one-row fallback then embeds its healthy rows and charges the failing one an attempt.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryEmbedderMarksABatchInOneTransactionTests : IDisposable
{
    private const string InducedFailureMessage = "induced failure for batch-transaction test";
    private readonly string _dataRoot = TestData.CreateTempRoot("entry-embedder-batch-tx");
    private readonly SqliteConnectionFactory _factory;

    public EntryEmbedderMarksABatchInOneTransactionTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task EmbedAsync_FailureInsideTheSecondBatch_RollsItBackThenRetriesItsRowsOneAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" }, cancellationToken: ct));

        var ids = new List<long>();
        for (var i = 0; i < 2 * EntryEmbedder.BatchSize; i++)
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                RETURNING id
                """,
                new { hash = $"row-{i}", path = $"p{i}.md", value = $"pending row {i}" }, cancellationToken: ct));
            ids.Add(id);
        }

        // Rows are id-ordered (SelectAllPendingForEmbed's ORDER BY id): batch 1 = ids[0..31],
        // batch 2 = ids[32..63]. ids[34] is the third row of batch 2.
        var failingId = ids[34];
        await InstallFailureTriggerAsync(connection, failingId, ct);

        var embedder = TestData.CreateEntryEmbedder(new CountingEmbeddingService(), Substitute.For<IModelMigrationLease>(),
            TimeProvider.System, new VecDimensionReconciler());

        var embedded = await embedder.EmbedPendingBatchAsync(connection, ids.Count, ct);

        embedded.ShouldBe(ids.Count - 1);
        (await EmbedStateAsync(connection, ids[0], ct)).ShouldBe("embedded",
            "batch 1 committed on its own before batch 2 ever started");
        (await EmbedStateAsync(connection, ids[31], ct)).ShouldBe("embedded",
            "batch 1's last row committed with the rest of batch 1");
        (await EmbedStateAsync(connection, ids[32], ct)).ShouldBe("embedded",
            "batch 2 rolled back cleanly, so its one-row retries could begin their own transactions");
        (await EmbedStateAsync(connection, ids[33], ct)).ShouldBe("embedded",
            "batch 2 rolled back cleanly, so its one-row retries could begin their own transactions");
        (await EmbedStateAsync(connection, failingId, ct)).ShouldBe("pending");
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT embed_attempts FROM entries WHERE id = @failingId", new { failingId }, cancellationToken: ct)))
            .ShouldBe(1L, "only the row that failed on its own is charged");
    }

    private static async Task<string?> EmbedStateAsync(SqliteConnection connection, long id, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT embed_state FROM entries WHERE id = @id", new { id }, cancellationToken: ct));

    /// <summary>Fails the row's MarkEmbedded (not its attempts count). RAISE(ABORT, ...) backs out the
    /// statement but leaves the surrounding BEGIN IMMEDIATE open for production code's own ROLLBACK.</summary>
    private static async Task InstallFailureTriggerAsync(SqliteConnection connection, long failingId,
        CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition($"""
                                                              CREATE TRIGGER test_force_fail_update_entries
                                                              BEFORE UPDATE OF embed_state ON entries
                                                              FOR EACH ROW WHEN NEW.id = {failingId}
                                                              BEGIN
                                                                  SELECT RAISE(ABORT, '{InducedFailureMessage}');
                                                              END;
                                                              """, cancellationToken: ct));
}
