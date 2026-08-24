using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP4 gate G4(a): a non-384 engine migrates end to end. The drain reconciles BOTH vec tables to
///     the engine's dimension and then refills them through the triggers, so counts reach parity with
///     the entries that were marked pending — the property that separates a real migration from one
///     that finishes green over empty tables.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NonDefaultDimensionMigrationTests
{
    private const int Dimension = 1024;

    [Fact]
    public async Task DrainMigrationAsync_With1024Engine_MovesBothTablesAndReachesRowParity()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await ConfigureEngineAsync(connection);
        await SeedPendingEntriesAsync(connection, 3);
        await OpenMigrationAsync(connection);

        var embedder = new EntryEmbedder(new FixedDimensionEmbeddingService(Dimension),
            new SqliteModelMigrationLease(TimeProvider.System), new FakeTimeProvider(), new VecDimensionReconciler());

        await embedder.DrainMigrationAsync(connection, Ct);

        (await TableSqlAsync(connection, "vec_entries")).ShouldContain($"float[{Dimension}]");
        (await TableSqlAsync(connection, "vec_structure")).ShouldContain($"float[{Dimension}]",
            customMessage: "vec_structure moves with vec_entries — a half-migrated pair is the G4 trap");
        (await CountAsync(connection, "SELECT count(*) FROM entries WHERE embed_state = 'embedded'")).ShouldBe(3);
        (await CountAsync(connection, "SELECT count(*) FROM vec_entries")).ShouldBe(3,
            "row parity: every re-embedded entry lands in the recreated table");
    }

    /// <summary>A 384 bank whose engine is still 384 must not be dropped and rebuilt for nothing.</summary>
    [Fact]
    public async Task DrainMigrationAsync_WithA384Engine_LeavesTheTablesAtTheirDeclaredDimension()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await ConfigureEngineAsync(connection);
        await SeedPendingEntriesAsync(connection, 1);
        await OpenMigrationAsync(connection);

        var embedder = new EntryEmbedder(new FixedDimensionEmbeddingService(384),
            new SqliteModelMigrationLease(TimeProvider.System), new FakeTimeProvider(), new VecDimensionReconciler());

        await embedder.DrainMigrationAsync(connection, Ct);

        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[384]");
        (await CountAsync(connection, "SELECT count(*) FROM vec_entries")).ShouldBe(1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An engine that embeds at a fixed dimension, standing in for a real non-384 model.</summary>
    private sealed class FixedDimensionEmbeddingService(int dimension) : IEmbeddingService
    {
        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) =>
            new FixedGenerator(dimension);

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => 254;

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => null;

        public string EngineFingerprint(string provider, string? model, string? baseUrl) => $"fixed:{dimension}";

        public int ResolveDimensions(EmbeddingSettings settings) => dimension;

        private sealed class FixedGenerator(int dimension) : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            {
                var items = values.ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
                foreach (var _ in items)
                {
                    embeddings.Add(new Embedding<float>(new float[dimension]));
                }

                return Task.FromResult(embeddings);
            }

            public void Dispose()
            {
            }

            object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;
        }
    }

    private static async Task ConfigureEngineAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES ('embedding.provider', 'local') " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value", cancellationToken: Ct));

    private static async Task SeedPendingEntriesAsync(SqliteConnection connection, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries(hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES (@hash, @value, 'p', 'project', 0, 0, 'pending')
                """, new { hash = $"h{i}", value = $"value {i}" }, cancellationToken: Ct));
        }
    }

    private static async Task OpenMigrationAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO model_migration(id, provider, model, base_url, engine, started_at, finished_at)
            VALUES (1, 'local', NULL, NULL, 'test-engine', 0, NULL)
            ON CONFLICT(id) DO UPDATE SET finished_at = NULL, started_at = 0
            """, cancellationToken: Ct));

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: Ct))
        ?? throw new InvalidOperationException($"{table} does not exist");

    private static async Task<long> CountAsync(SqliteConnection connection, string sql) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: Ct));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
