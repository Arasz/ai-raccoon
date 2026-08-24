using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A scriptable <see cref="ICodeEmbedder" /> for <c>SqliteCodeSearchService</c> tests: lets a
///     test hand back an exact query vector (for controlled vec0 KNN fixtures) or throw
///     <see cref="AiRaccoon.Core.Memory.Code.CodeEngineUnloadableException" /> without seeding a
///     real manifest directory on disk.
/// </summary>
public sealed class FakeCodeEmbedder : ICodeEmbedder
{
    public QueryVector QueryVectorToReturn { get; set; } = QueryVector.Empty;

    public Exception? ThrowOnEmbedQuery { get; set; }

    public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        if (ThrowOnEmbedQuery is { } ex)
        {
            throw ex;
        }

        return Task.FromResult(QueryVectorToReturn);
    }

    public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
        CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
