using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Review round on PR #530, finding F1: <see cref="EntryEmbedder.EmbedPendingBatchAsync" />
///     must return the count of rows whose <c>MarkEmbedded</c> UPDATE actually landed, not the
///     count it read at SELECT time — <see cref="EmbedDrainService" />'s no-spin safety argument
///     for the re-signal (WP1) depends on it, and it was already true for
///     <see cref="CodeEmbedder" /> but not here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryEmbedderAffectedRowCountTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("entry-embedder-row-count");
    private readonly SqliteConnectionFactory _factory;

    public EntryEmbedderAffectedRowCountTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     One of three SELECTed rows is deleted before its UPDATE runs — a real race (a concurrent
    ///     purge or scope change), not a hypothetical. The returned count must reflect the two
    ///     UPDATEs that landed, not the three rows read.
    /// </summary>
    [RetryFact]
    public async Task ARowVanishesBetweenSelectAndUpdate_ReturnsOnlyRowsWhoseUpdateLanded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" }, cancellationToken: ct));

        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
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

        var embeddings = new VanishingRowEmbeddingService(connection, ids[1]);
        var embedder = new EntryEmbedder(embeddings, Substitute.For<IModelMigrationLease>(), TimeProvider.System, new VecDimensionReconciler());

        var affected = await embedder.EmbedPendingBatchAsync(connection, 3, ct);

        affected.ShouldBe(2, "the vanished row's UPDATE affected zero rows and must not be counted");
    }

    /// <summary>Deletes one target row the first time the generator runs — the window between the
    /// batch's SELECT and its per-row UPDATE.</summary>
    private sealed class VanishingRowEmbeddingService(SqliteConnection connection, long vanishingId) : IEmbeddingService
    {
        private readonly VanishingRowGenerator _generator = new(connection, vanishingId);

        public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
            $"test:{provider}:{model}@{baseUrl}";

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) => _generator;

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => 512;

        public int ResolveDimensions(EmbeddingSettings settings) => 384;

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => null;

        private sealed class VanishingRowGenerator(SqliteConnection connection, long vanishingId)
            : IEmbeddingGenerator<string, Embedding<float>>
        {
            private bool _deleted;

            public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(values);
                var items = values.ToList();

                if (!_deleted)
                {
                    _deleted = true;
                    await connection.ExecuteAsync(new CommandDefinition("DELETE FROM entries WHERE id = @id",
                        new { id = vanishingId }, cancellationToken: cancellationToken));
                }

                var generated = new GeneratedEmbeddings<Embedding<float>>(items.Count);
                foreach (var _ in items)
                {
                    generated.Add(new Embedding<float>(new float[384]));
                }

                return generated;
            }

            public void Dispose()
            {
            }

            object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;
        }
    }
}
