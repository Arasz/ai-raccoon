using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Shared <see cref="SqliteMemoryStore" /> construction for the search-phase timing suites —
///     S1's attribution tests (<see cref="SqliteMemoryStoreSearchTimingsTests" />) and S2's closure
///     gate consume this one factory and stub rather than each keeping its own copy
///     (derive-or-delete-the-list; docs/plans/2026-08-17-search-phase-attribution.md §3).
/// </summary>
internal static class SearchTimingsHarness
{
    public static SqliteMemoryStore CreateStore(ISqliteConnectionFactory factory, TimeProvider timeProvider,
        IEntryEmbedder? embedder = null)
    {
        embedder ??= new EntryEmbedder(TestData.CreateEmbeddingService());
        return new SqliteMemoryStore(factory, new SqliteMemorySourceStore(factory),
            new FileIngestor(new FileTypeMatcher([]), embedder, new SqliteMemorySourceStore(factory), timeProvider),
            embedder, timeProvider, NullLogger<SqliteMemoryStore>.Instance,
            new NoiseFilteringService([]), new SqliteSettingsStore(factory));
    }

    /// <summary>
    ///     A fixed 384-float vector, so the vector modality actually queries without a live embedding
    ///     endpoint. <paramref name="delay" /> lets S2's closure gate put a known-size cost inside
    ///     <c>search.embed</c> (a real await, on the real clock — not a scripted value).
    /// </summary>
    public sealed class VectorEmbedderStub(TimeSpan delay = default) : IEntryEmbedder
    {
        public Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
            string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");

        public async Task<byte[]?> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return EmbeddingBlob.ToBytes(new float[384]);
        }

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by SearchAsync.");
    }
}
