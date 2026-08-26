using System.Globalization;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The bank's embedding mechanics: read the engine settings, embed one row or a batch, and
///     re-embed everything when the engine changes. Takes an open connection rather than opening
///     its own, since every caller is already inside one and embedding is never its own transaction.
/// </summary>
public sealed class EntryEmbedder(
    IEmbeddingService embeddings,
    IModelMigrationLease migrationLease,
    TimeProvider timeProvider,
    IVecDimensionReconciler vecDimensions,
    EmbedDrainReporter reporter,
    IOperationTelemetry telemetry,
    ILogger<EntryEmbedder> logger) : IEntryEmbedder
{
    /// <summary>Rows per generator call. Internal so PendingEmbedJob can derive its own per-run bound from it instead of duplicating the number.</summary>
    internal const int BatchSize = 32;

    private const string BundledModel = "bundled";

    /// <summary>The open migration (its started_at) whose blank-provider state already produced
    /// the 1012 Warning — one per process per migration, so the 15s relay poll cannot flood (M8).</summary>
    private long? _warnedNoProviderMigration;

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
        string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previous = await ReadSettingAsync(connection, EmbeddingSettingsKeys.Engine, cancellationToken)
            .ConfigureAwait(false);
        var engine = embeddings.EngineFingerprint(provider, model, baseUrl);

        if (previous is null || string.Equals(previous, engine, StringComparison.Ordinal))
        {
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

            await connection.ExecuteAsync(Def(MemorySql.MarkAllEmbeddedPending, cancellationToken, transaction))
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelMigrationInProgressException)
        {
            throw;
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

        const EmbedCorpus corpus = EmbedCorpus.Memory;

        // The lease's pre-state, read BEFORE acquiring: after acquisition the row carries OUR
        // owner, so the previous holder is only knowable here (1009, LANE P4).
        var preState = await ReadOpenMigrationStateAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!await migrationLease.TryAcquireAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            reporter.MigrationLeaseHeld(logger, corpus);
            return false;
        }

        using var pass = telemetry.Begin(EmbedDrainService.OperationName);
        var startedAt = timeProvider.GetTimestamp();
        var drained = 0;
        try
        {
            // The open-migration re-check stays UNDER the lease (S7): acquiring first is what
            // makes it race-free. False here means the migration finished between the relay's
            // due-check and this pass.
            var open = await connection.QuerySingleOrDefaultAsync<long?>(
                Def(MemorySql.HasOpenModelMigration, cancellationToken)).ConfigureAwait(false) > 0;
            if (!open)
            {
                reporter.MigrationAlreadyFinished(logger, corpus);
                return false;
            }

            if (!await HasProviderAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                if (preState?.StartedAt != _warnedNoProviderMigration)
                {
                    _warnedNoProviderMigration = preState?.StartedAt;
                    reporter.MigrationNoProvider(logger, corpus);
                }

                // Observability only (M8): the drain keeps throwing — the bank stays ToolGate-locked
                // until the model-reset guard closes or refuses the migration, a separate follow-up.
                throw new InvalidOperationException(
                    "ai-raccoon: no embedding provider is configured; the open model migration cannot drain " +
                    "and the bank stays ToolGate-locked until a provider is set or the migration is closed");
            }

            var owed = await connection.ExecuteScalarAsync<long>(Def(MemorySql.CountPendingEmbed, cancellationToken))
                .ConfigureAwait(false);
            reporter.MigrationStarted(logger, corpus, owed);

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

                drained += await EmbedAsync(connection, batch, cancellationToken).ConfigureAwait(false);
                await migrationLease.TryRenewAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(Def(MemorySql.FinishModelMigration,
                    new { finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() }, cancellationToken))
                .ConfigureAwait(false);
            if (drained > 0)
            {
                pass.NoteWork();
            }

            // RecordRows MUST run before Succeeded() (#548 review, B1) — the same rule as
            // EmbedDrainService.DrainOnceAsync: Succeeded() claims the scope's one measurement.
            pass.RecordRows(drained);
            pass.Succeeded();
            reporter.PassFinished(logger, corpus, drained, timeProvider.GetElapsedTime(startedAt));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pass.Failed(ex);
            reporter.PassFailed(logger, corpus, ex);
            throw;
        }
        finally
        {
            await migrationLease.ReleaseAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return;
        }

        await vecDimensions.ReconcileMemoryAsync(connection, embeddings.ResolveDimensions(settings), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Embeds a set of rows with the configured engine; missing rows are skipped. One
    ///     <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c> per <c>BatchSize</c> sub-batch around that batch's
    ///     <c>MarkEmbedded</c> writes (WP12 Fix B) — inference stays outside it, so a BUSY write lock
    ///     costs at most one batch's already-done inference, not every row this call has marked so
    ///     far.
    /// </summary>
    private async Task<int> EmbedAsync(SqliteConnection connection, IReadOnlyList<EmbedRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        var generator = embeddings.CreateGenerator(settings);
        var affected = 0;
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            var batch = rows.Skip(offset).Take(BatchSize).ToList();
            var result = await generator.GenerateAsync(batch.Select(r => r.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var headingPaths = batch.Select(r => HeadingPathParser.Parse(r.Value)).ToList();
            var structure = await EmbedDistinctHeadingsAsync(generator, headingPaths, cancellationToken).ConfigureAwait(false);
            var embeddingBlobs = result.Select(r => EmbeddingBlob.ToBytes(r.Vector)).ToList();

            await connection.ExecuteAsync(
                    new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            try
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    var headingPath = headingPaths[i];
                    structure.TryGetValue(headingPath, out var structureEmbedding);
                    affected += await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
                            new
                            {
                                id = batch[i].Id,
                                embedding = embeddingBlobs[i],
                                headingPath,
                                structureEmbedding
                            },
                            cancellationToken))
                        .ConfigureAwait(false);
                }

                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                throw;
            }
        }

        return affected;
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

    private static async Task<bool> HasProviderAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await ReadSettingAsync(connection, EmbeddingSettingsKeys.Provider, cancellationToken)
            .ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(provider);
    }

    private static async Task<OpenMigrationState?> ReadOpenMigrationStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<OpenMigrationState>(
            Def(MemorySql.SelectOpenModelMigrationLease, cancellationToken)).ConfigureAwait(false);

    /// <summary>The open migration's pre-acquisition lease state (LANE P4): the previous holder
    /// and the row's age, read before the lease is taken — after acquisition the row carries our
    /// owner, so the pre-state is the only place 1009's facts exist.</summary>
    internal sealed record OpenMigrationState
    {
        public string? LeaseOwner { get; init; }

        public long? LeaseExpiresAt { get; init; }

        public long? StartedAt { get; init; }

        public bool LeaseWasStale(long now) =>
            LeaseOwner is not null && LeaseExpiresAt is { } expiresAt && expiresAt < now;

        public TimeSpan Age(long now) => TimeSpan.FromSeconds(Math.Max(0, now - (StartedAt ?? now)));
    }

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
