using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The bank's embedding mechanics: read the engine settings, embed one row or a batch, and
///     re-embed everything when the engine changes. Takes an open connection rather than opening
///     its own — every caller is already inside one, and embedding is never a transaction of its
///     own (WI-8).
/// </summary>
internal sealed class EntryEmbedder(EmbeddingService embeddings)
{
    public const int BatchSize = 32;
    private const string BundledModel = "bundled";

    /// <summary>Writes the engine settings and, when the engine fingerprint changed, re-embeds the whole bank.</summary>
    public async Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
        string? baseUrl, CancellationToken cancellationToken)
    {
        var previous = await ReadSettingAsync(connection, EmbeddingSettingsKeys.Engine, cancellationToken)
            .ConfigureAwait(false);

        await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Provider, provider, cancellationToken)
            .ConfigureAwait(false);
        await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Model, model, cancellationToken)
            .ConfigureAwait(false);
        await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.BaseUrl, baseUrl, cancellationToken)
            .ConfigureAwait(false);

        var engine = EmbeddingService.EngineFingerprint(provider, model, baseUrl);
        await connection.ExecuteAsync(Def(MemorySql.UpsertSetting,
            new { key = EmbeddingSettingsKeys.Engine, value = engine }, cancellationToken)).ConfigureAwait(false);

        if (string.Equals(previous, engine, StringComparison.Ordinal))
        {
            return new EmbeddingConfig(provider, model ?? BundledModel, engine);
        }

        var reEmbed = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectAllEmbedded, cancellationToken))
            .ConfigureAwait(false)).ToList();
        await EmbedAsync(connection, reEmbed, cancellationToken).ConfigureAwait(false);

        return new EmbeddingConfig(provider, model ?? BundledModel, engine);
    }

    /// <summary>Embeds one row when an engine is configured; a bank with no engine is left pending.</summary>
    public async Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
        CancellationToken cancellationToken)
    {
        var provider = await ReadSettingAsync(connection, EmbeddingSettingsKeys.Provider, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var generator = embeddings.CreateGenerator(settings);
        var result = await generator.GenerateAsync([value], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
            new { id, embedding = EmbeddingBlob.ToBytes(result[0].Vector) }, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Embeds a project's pending rows in batches with the configured engine.</summary>
    public async Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        while (true)
        {
            var remaining = (limit ?? int.MaxValue) - processed;
            if (remaining <= 0)
            {
                break;
            }

            var batch = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectPendingForEmbed,
                    new { projectId, limit = Math.Min(BatchSize, remaining) }, cancellationToken))
                .ConfigureAwait(false)).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            processed += await EmbedAsync(connection, batch, cancellationToken).ConfigureAwait(false);
        }

        return processed;
    }

    /// <summary>Embeds a set of rows with the configured engine; missing rows are skipped.</summary>
    private async Task<int> EmbedAsync(SqliteConnection connection, IReadOnlyList<EmbedRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var generator = embeddings.CreateGenerator(settings);
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            var batch = rows.Skip(offset).Take(BatchSize).ToList();
            var result = await generator.GenerateAsync(batch.Select(r => r.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
                        new { id = batch[i].Id, embedding = EmbeddingBlob.ToBytes(result[i].Vector) },
                        cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        return rows.Count;
    }

    /// <summary>Embeds a query string, or null when the bank has no engine — search degrades rather than failing.</summary>
    public async Task<byte[]?> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return null;
        }

        var generator = embeddings.CreateGenerator(settings);
        var embedding = await generator.GenerateAsync([query], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return EmbeddingBlob.ToBytes(embedding[0].Vector);
    }

    public async Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        new(await ReadSettingAsync(connection, EmbeddingSettingsKeys.Provider, cancellationToken)
                .ConfigureAwait(false) ?? "",
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.Model, cancellationToken).ConfigureAwait(false),
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.BaseUrl, cancellationToken).ConfigureAwait(false),
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.ApiKey, cancellationToken).ConfigureAwait(false));

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
            Def(MemorySql.SelectSetting, new { key }, cancellationToken)).ConfigureAwait(false);

    private static async Task UpsertOrDeleteAsync(SqliteConnection connection, string key, string? value,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(value is null
            ? Def(MemorySql.DeleteSetting, new { key }, cancellationToken)
            : Def(MemorySql.UpsertSetting, new { key, value }, cancellationToken)).ConfigureAwait(false);

    private static CommandDefinition Def(string sql, object? parameters, CancellationToken cancellationToken) => new(sql, parameters, cancellationToken: cancellationToken);

    private static CommandDefinition Def(string sql, CancellationToken cancellationToken) => new(sql, cancellationToken: cancellationToken);

    internal sealed record EmbedRow
    {
        public long Id { get; init; }

        public string Value { get; init; } = "";
    }
}
