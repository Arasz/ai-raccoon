using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Shared <see cref="SqliteMemoryStore" /> construction for the search-phase timing suites —
///     S1's attribution tests (<see cref="SqliteMemoryStoreSearchTimingsTests" />) and S2's closure
///     gate consume this one factory and stub rather than each keeping its own copy
///     (derive-or-delete-the-list; docs/plans/2026-08-17-search-phase-attribution.md §3).
/// </summary>
internal static class SearchTimingsHarness
{
    private static readonly IModelMigrationLease ModelMigrationLease = Substitute.For<IModelMigrationLease>();
    private static readonly TimeProvider TimeProvider = new FakeTimeProvider();

    public static SqliteMemoryStore CreateStore(ISqliteConnectionFactory factory, TimeProvider timeProvider,
        IEntryEmbedder? embedder = null)
    {
        embedder ??= new EntryEmbedder(TestData.CreateEmbeddingService(), ModelMigrationLease, TimeProvider);
        var pump = TestData.NewEmbedDrainPump();
        return new SqliteMemoryStore(factory, new SqliteMemorySourceStore(factory),
            TestData.NewFileIngestor(new FileTypeMatcher([]), new SqliteMemorySourceStore(factory), timeProvider,
                TestData.CreateEmbeddingService(), embedDrainPump: pump),
            embedder, timeProvider, NullLogger<SqliteMemoryStore>.Instance,
            new NoiseFilteringService([]), new SqliteSettingsStore(factory), pump);
    }

    /// <summary>
    ///     A fixed 384-float vector, so the vector modality actually queries without a live embedding
    ///     endpoint. <paramref name="onEmbedQuery" /> lets S2's closure gate put a known-size cost
    ///     inside <c>search.embed</c> by advancing a controlled clock — the cost is exact and
    ///     load-independent, where a real <c>Task.Delay</c> on the system clock was neither.
    /// </summary>
    public sealed class VectorEmbedderStub(Action? onEmbedQuery = null) : IEntryEmbedder
    {
        public Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
            string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken)
        {
            onEmbedQuery?.Invoke();
            return Task.FromResult(new QueryVector(EmbeddingBlob.ToBytes(new float[384])));
        }

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");
    }
}
