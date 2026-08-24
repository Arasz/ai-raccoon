using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP12 Fix B: <see cref="EntryEmbedder.EmbedAsync" /> used to <c>MarkEmbedded</c> one row at a
///     time, autocommit — a BUSY mid-batch threw away every row already marked in that pass, not
///     just the rest of the batch. One <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c> per <c>BatchSize</c>-sized
///     sub-batch bounds the loss to at most one batch's inference.
///     <para>
///         Proven by effect, not a connection spy — <see cref="SqliteConnection" /> is not designed
///         to be intercepted, and Microsoft.Data.Sqlite exposes no command-trace hook — so a real
///         trigger aborts the third row's UPDATE inside the SECOND batch. The two rows before it in
///         that same batch must roll back too (today they would not: each row commits on its own),
///         while the first batch — already committed by its own earlier COMMIT before the second
///         batch even starts — stays marked.
///     </para>
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

    [Fact]
    public async Task EmbedAsync_FailureInsideTheSecondBatch_RollsBackOnlyThatBatch()
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

        var embedder = new EntryEmbedder(new CountingEmbeddingService(), Substitute.For<IModelMigrationLease>(),
            TimeProvider.System, new VecDimensionReconciler());

        await Should.ThrowAsync<SqliteException>(() => embedder.EmbedPendingBatchAsync(connection, ids.Count, ct));

        (await EmbedStateAsync(connection, ids[0], ct)).ShouldBe("embedded",
            "batch 1 committed on its own before batch 2 ever started");
        (await EmbedStateAsync(connection, ids[31], ct)).ShouldBe("embedded",
            "batch 1's last row committed with the rest of batch 1");
        (await EmbedStateAsync(connection, ids[32], ct)).ShouldBe("pending",
            "batch 2's own failure must roll back rows batch 2 already wrote, not just the row that failed");
        (await EmbedStateAsync(connection, ids[33], ct)).ShouldBe("pending",
            "batch 2's own failure must roll back rows batch 2 already wrote, not just the row that failed");
    }

    private static async Task<string?> EmbedStateAsync(SqliteConnection connection, long id, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT embed_state FROM entries WHERE id = @id", new { id }, cancellationToken: ct));

    /// <summary>A real (non-TEMP) trigger, matching the induced-failure pattern in
    /// DeleteReplaceRollbackTests — RAISE(ABORT, ...) backs out the failed statement's own effect but
    /// leaves the surrounding BEGIN IMMEDIATE transaction, if any, open for production code's own
    /// ROLLBACK.</summary>
    private static async Task InstallFailureTriggerAsync(SqliteConnection connection, long failingId,
        CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition($"""
                                                              CREATE TRIGGER test_force_fail_update_entries
                                                              BEFORE UPDATE ON entries
                                                              FOR EACH ROW WHEN NEW.id = {failingId}
                                                              BEGIN
                                                                  SELECT RAISE(ABORT, '{InducedFailureMessage}');
                                                              END;
                                                              """, cancellationToken: ct));
}
