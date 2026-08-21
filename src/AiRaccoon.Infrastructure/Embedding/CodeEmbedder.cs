using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

/// <inheritdoc cref="ICodeEmbedder" />
public sealed class CodeEmbedder(IEmbeddingService embeddings) : ICodeEmbedder
{
    /// <summary>Rows per drain batch (§3.3 D-E9: "batches of 32").</summary>
    internal const int BatchSize = 32;

    public async Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return QueryVector.Empty;
        }

        var settings = SettingsFor(codeModel);
        try
        {
            var trimmed = embeddings.TrimQueryToWindow(settings, query);
            var generator = embeddings.CreateGenerator(settings);
            var result = await generator.GenerateAsync([trimmed], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new QueryVector(EmbeddingBlob.ToBytes(result[0].Vector))
            {
                Trimmed = !string.Equals(trimmed, query, StringComparison.Ordinal)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodeEngineUnloadableException(codeModel, ex);
        }
    }

    public async Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
        CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return 0;
        }

        var batch = (await connection.QueryAsync<EmbedRow>(new CommandDefinition(
                MemorySql.SelectAllPendingCodeForEmbed, new { limit }, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();
        if (batch.Count == 0)
        {
            return 0;
        }

        var settings = SettingsFor(codeModel);
        try
        {
            var generator = embeddings.CreateGenerator(settings);
            var result = await generator.GenerateAsync(batch.Select(row => row.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkCodeEmbedded,
                    new { id = batch[i].Id, embedding = EmbeddingBlob.ToBytes(result[i].Vector) },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            return batch.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodeEngineUnloadableException(codeModel, ex);
        }
    }

    public async Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return false; // no engine configured: a pending row here is legitimately unembeddable
        }

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                MemorySql.HasPendingCodeEmbed, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>The code corpus always activates local + a manifest directory (§3.3) — no openai/remote code engine in v1.</summary>
    private static EmbeddingSettings SettingsFor(string codeModel) => new("local", codeModel, null, null);

    private static async Task<string?> ReadCodeModelAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(MemorySql.SelectSetting,
            new { key = EmbeddingSettingsKeys.CodeModel }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private sealed record EmbedRow
    {
        public long Id { get; init; }

        public string Value { get; init; } = "";
    }
}
