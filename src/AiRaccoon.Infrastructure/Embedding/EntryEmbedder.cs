using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The bank's embedding mechanics: read the engine settings, embed one row or a batch, and
///     re-embed everything when the engine changes. Takes an open connection rather than opening
///     its own, since every caller is already inside one and embedding is never its own transaction.
/// </summary>
public sealed class EntryEmbedder(IEmbeddingService embeddings) : IEntryEmbedder
{
    private const int BatchSize = 32;
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
        var headingPath = HeadingPathParser.Parse(value);

        // One generator call carrying both inputs when a heading exists (was two): a per-chunk
        // ingest loop turns a second call per row into a doubled inference count per document.
        var result = headingPath.Length > 0
            ? await generator.GenerateAsync([value, headingPath], cancellationToken: cancellationToken)
                .ConfigureAwait(false)
            : await generator.GenerateAsync([value], cancellationToken: cancellationToken).ConfigureAwait(false);
        var structureEmbedding = headingPath.Length > 0 ? EmbeddingBlob.ToBytes(result[1].Vector) : null;

        await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
                new
                {
                    id,
                    embedding = EmbeddingBlob.ToBytes(result[0].Vector),
                    headingPath,
                    structureEmbedding
                }, cancellationToken))
            .ConfigureAwait(false);
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

        // Structure heal shares the pending-embed loop's `limit` budget above, so a small
        // `limit` bounds both instead of leaving structure backfill to run unbounded.
        var healBudget = (limit ?? int.MaxValue) - processed;
        if (healBudget > 0)
        {
            await HealStructureAsync(connection, projectId, healBudget, cancellationToken).ConfigureAwait(false);
        }

        return processed;
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

            // Heading paths repeat heavily within a document; embedding only the distinct non-empty
            // ones once per batch is what keeps this affordable (docs/plans/2026-08-08-search-knn-perf.md §3.6).
            var headingPaths = batch.Select(r => HeadingPathParser.Parse(r.Value)).ToList();
            var structure = await EmbedDistinctHeadingsAsync(generator, headingPaths, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < batch.Count; i++)
            {
                var headingPath = headingPaths[i];
                structure.TryGetValue(headingPath, out var structureEmbedding);
                await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
                        new
                        {
                            id = batch[i].Id,
                            embedding = EmbeddingBlob.ToBytes(result[i].Vector),
                            headingPath,
                            structureEmbedding
                        },
                        cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        return rows.Count;
    }

    /// <summary>
    ///     Backfills structure vectors for rows embedded before the structure writer existed, up
    ///     to <paramref name="budget" /> candidates: every candidate a batch touches gets a real
    ///     heading path or the '' sentinel, which removes it from SelectStructureHealCandidates'
    ///     WHERE clause, so each iteration shrinks the candidate set and the loop terminates.
    /// </summary>
    private async Task HealStructureAsync(SqliteConnection connection, string projectId, int budget,
        CancellationToken cancellationToken)
    {
        var remaining = budget;
        while (remaining > 0)
        {
            var candidates = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectStructureHealCandidates,
                    new { projectId, limit = Math.Min(BatchSize, remaining) }, cancellationToken))
                .ConfigureAwait(false)).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
            var generator = embeddings.CreateGenerator(settings);
            var headingPaths = candidates.Select(r => HeadingPathParser.Parse(r.Value)).ToList();
            var structure = await EmbedDistinctHeadingsAsync(generator, headingPaths, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < candidates.Count; i++)
            {
                var headingPath = headingPaths[i];
                structure.TryGetValue(headingPath, out var structureEmbedding);
                await connection.ExecuteAsync(Def(MemorySql.MarkStructure,
                        new { id = candidates[i].Id, headingPath, structureEmbedding }, cancellationToken))
                    .ConfigureAwait(false);
            }

            remaining -= candidates.Count;
        }
    }

    /// <summary>Embeds each distinct non-empty heading path once; empty paths are omitted from the result.</summary>
    private static async Task<Dictionary<string, byte[]>> EmbedDistinctHeadingsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<string> headingPaths,
        CancellationToken cancellationToken)
    {
        var distinct = headingPaths.Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var result = await generator.GenerateAsync(distinct, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var vectors = new Dictionary<string, byte[]>(distinct.Count, StringComparer.Ordinal);
        for (var i = 0; i < distinct.Count; i++)
        {
            vectors[distinct[i]] = EmbeddingBlob.ToBytes(result[i].Vector);
        }

        return vectors;
    }

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
