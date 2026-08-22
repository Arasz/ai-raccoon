using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP12-A — mirrors CodeEmbedderTests' length-sort test for the memory corpus:
///     <see cref="EntryEmbedder" />'s private EmbedAsync (reached here via
///     <see cref="EntryEmbedder.EmbedPendingBatchAsync" />) sub-batches rows selected in id order
///     (MemorySql.SelectAllPendingForEmbed). Sorting by length before the BatchSize slice keeps
///     each generator call length-homogeneous.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryEmbedderBatchSortTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("entry-embedder-batch-sort");
    private readonly SqliteConnectionFactory _factory;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public EntryEmbedderBatchSortTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task EmbedPendingBatchAsync_MixedLengthRows_SortsByLengthBeforeBatching()
    {
        var counting = new CountingEmbeddingService();
        var embedder = new EntryEmbedder(counting, _modelMigrationLease, _timeProvider);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ConfigureProviderAsync(connection);

        var values = new List<string>();
        for (var id = 1; id <= 64; id++)
        {
            // Alternating short/long lengths, all distinct, so id order != length order.
            var length = id % 2 == 0 ? 2000 + id : 50 + id;
            var value = new string('a', length);
            values.Add(value);
            await SeedPendingRowAsync(connection, value);
        }

        var processed = await embedder.EmbedPendingBatchAsync(connection, 64, TestContext.Current.CancellationToken);

        processed.ShouldBe(64);
        counting.Calls.Count.ShouldBe(2);
        var sortedByLength = values.OrderBy(v => v.Length).ToList();
        counting.Calls[0].ShouldBe(sortedByLength.Take(32).ToList(), ignoreOrder: true,
            "batch 1 must be exactly the 32 shortest rows");
        counting.Calls[1].ShouldBe(sortedByLength.Skip(32).ToList(), ignoreOrder: true,
            "batch 2 must be exactly the remaining 32 rows");
        var states = await connection.QueryAsync<string>("SELECT embed_state FROM entries ORDER BY id");
        states.ShouldAllBe(s => s == "embedded", "every row must be marked embedded exactly once");
    }

    private static async Task ConfigureProviderAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" });

    private static async Task SeedPendingRowAsync(SqliteConnection connection, string value) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
            VALUES (@hash, @path, @value, 'project', @projectId, 0, 0)
            """,
            new { hash = Guid.NewGuid().ToString("N"), path = "p.md", value, projectId = ProjectId });
}
