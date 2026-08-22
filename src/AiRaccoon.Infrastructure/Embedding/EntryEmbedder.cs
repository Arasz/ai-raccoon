using System.Globalization;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Maintenance;
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
public sealed class EntryEmbedder(IEmbeddingService embeddings, IModelMigrationLease migrationLease,
    TimeProvider timeProvider, IVecDimensionReconciler? vecDimensions = null) : IEntryEmbedder
{
    /// <summary>Rows per generator call. Internal so PendingEmbedJob can derive its own per-run bound from it instead of duplicating the number.</summary>
    internal const int BatchSize = 32;

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

        var engine = embeddings.EngineFingerprint(provider, model, baseUrl);
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

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
        string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previous = await ReadSettingAsync(connection, EmbeddingSettingsKeys.Engine, cancellationToken)
            .ConfigureAwait(false);
        var engine = embeddings.EngineFingerprint(provider, model, baseUrl);

        // A null `previous` is a bank being configured for the first time, not a migration: there is
        // no prior engine's vectors to make stale, so there is nothing to owe and no lease to take.
        if (previous is null || string.Equals(previous, engine, StringComparison.Ordinal))
        {
            // Nothing owed: still apply the discrete settings rows, no outbox row needed.
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Provider, provider, cancellationToken)
                .ConfigureAwait(false);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Model, model, cancellationToken)
                .ConfigureAwait(false);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.BaseUrl, baseUrl, cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(Def(MemorySql.UpsertSetting,
                    new { key = EmbeddingSettingsKeys.Engine, value = engine }, cancellationToken))
                .ConfigureAwait(false);
            return new EmbeddingConfig(provider, model ?? BundledModel, engine);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Provider, provider, cancellationToken, transaction)
                .ConfigureAwait(false);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Model, model, cancellationToken, transaction)
                .ConfigureAwait(false);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.BaseUrl, baseUrl, cancellationToken, transaction)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(Def(MemorySql.UpsertSetting,
                    new { key = EmbeddingSettingsKeys.Engine, value = engine }, cancellationToken, transaction))
                .ConfigureAwait(false);

            var started = await connection.ExecuteAsync(Def(MemorySql.StartModelMigration,
                    new { provider, model, baseUrl, engine, startedAt = now.ToUnixTimeSeconds() }, cancellationToken,
                    transaction))
                .ConfigureAwait(false);
            if (started == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new ModelMigrationInProgressException(
                    "ai-raccoon: a model migration is already in progress; wait for it to finish before starting another");
            }

            // The outbox's payoff: every row this touches leaves the searchable vec0 index the
            // instant this commits (vec_entries_pending/vec_structure_pending), so a crash right
            // after COMMIT degrades to FTS5-only for those rows rather than mixing two models' vectors.
            await connection.ExecuteAsync(Def(MemorySql.MarkAllEmbeddedPending, cancellationToken, transaction))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelMigrationInProgressException)
        {
            throw; // already rolled back above
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new EmbeddingConfig(provider, model ?? BundledModel, engine);
    }

    /// <inheritdoc />
    public async Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (migrationLease is null)
        {
            throw new InvalidOperationException(
                "ai-raccoon: EntryEmbedder was built without an IModelMigrationLease; DrainMigrationAsync needs one");
        }

        if (!await migrationLease.TryAcquireAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return false; // another relay already owns it
        }

        try
        {
            var open = await connection.QuerySingleOrDefaultAsync<long?>(
                Def(MemorySql.HasOpenModelMigration, cancellationToken)).ConfigureAwait(false) > 0;
            if (!open)
            {
                return false; // finished by another relay between the caller's check and our lease
            }

            // Phase 1 (D3): bring vec0 to the new engine's dimension BEFORE the first row is
            // embedded. After the loop the DROP would discard everything just written, and the rows
            // are no longer pending, so nothing re-drives them — a green migration over empty tables.
            await ReconcileVecDimensionsAsync(connection, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectAllPendingForEmbed,
                    new { limit = BatchSize }, cancellationToken)).ConfigureAwait(false)).ToList();
                if (batch.Count == 0)
                {
                    break;
                }

                await EmbedAsync(connection, batch, cancellationToken).ConfigureAwait(false);
                await migrationLease.TryRenewAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(Def(MemorySql.FinishModelMigration,
                    new { finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() }, cancellationToken))
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            await migrationLease.ReleaseAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var reconciler = vecDimensions ?? new VecDimensionReconciler();
        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return;
        }

        await reconciler.ReconcileAsync(connection, embeddings.ResolveDimensions(settings), cancellationToken)
            .ConfigureAwait(false);
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

    /// <summary>
    ///     Embeds up to <paramref name="limit" /> bank-wide pending rows (not project-scoped, like
    ///     <see cref="DrainMigrationAsync" />'s own loop) — a single bounded batch rather than a full
    ///     drain, for <see cref="PendingEmbedJob" />'s on-demand sweep.
    /// </summary>
    public async Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
        CancellationToken cancellationToken)
    {
        var batch = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectAllPendingForEmbed,
            new { limit }, cancellationToken)).ConfigureAwait(false)).ToList();
        return await EmbedAsync(connection, batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Embeds a query string, or null when the bank has no engine — search degrades rather than failing.</summary>
    public async Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return QueryVector.Empty;
        }

        var generator = embeddings.CreateGenerator(settings);
        var embedded = embeddings.TrimQueryToWindow(settings, query);
        var embedding = await generator.GenerateAsync([embedded], cancellationToken: cancellationToken).ConfigureAwait(false);
        return new QueryVector(EmbeddingBlob.ToBytes(embedding[0].Vector));
    }

    public async Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        new(await ReadSettingAsync(connection, EmbeddingSettingsKeys.Provider, cancellationToken)
                .ConfigureAwait(false) ?? "",
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.Model, cancellationToken).ConfigureAwait(false),
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.BaseUrl, cancellationToken).ConfigureAwait(false),
            await ReadSettingAsync(connection, EmbeddingSettingsKeys.ApiKey, cancellationToken).ConfigureAwait(false),
            int.TryParse(
                await ReadSettingAsync(connection, EmbeddingSettingsKeys.Dimensions, cancellationToken)
                    .ConfigureAwait(false),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions)
                ? dimensions
                : null);

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
        CancellationToken cancellationToken, SqliteTransaction? transaction = null) =>
        await connection.ExecuteAsync(value is null
            ? Def(MemorySql.DeleteSetting, new { key }, cancellationToken, transaction)
            : Def(MemorySql.UpsertSetting, new { key, value }, cancellationToken, transaction)).ConfigureAwait(false);

    private static CommandDefinition Def(string sql, object? parameters, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static CommandDefinition Def(string sql, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        new(sql, transaction: transaction, cancellationToken: cancellationToken);

    internal sealed record EmbedRow
    {
        public long Id { get; init; }

        public string Value { get; init; } = "";
    }
}
