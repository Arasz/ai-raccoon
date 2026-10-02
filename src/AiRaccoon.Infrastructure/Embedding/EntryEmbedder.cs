using System.Globalization;
using System.Runtime.ExceptionServices;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using static AiRaccoon.Infrastructure.Sqlite.Sql;
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
public sealed partial class EntryEmbedder(
    IEmbeddingService embeddings,
    IModelMigrationLease migrationLease,
    TimeProvider timeProvider,
    IVecDimensionReconciler vecDimensions,
    IChunkBudgetReconciler chunkBudgets,
    EmbedDrainReporter reporter,
    IOperationTelemetry telemetry,
    ILogger<EntryEmbedder> logger) : IEntryEmbedder
{
    /// <summary>Rows per generator call. Internal so PendingEmbedJob can derive its own per-run bound from it instead of duplicating the number.</summary>
    internal const int BatchSize = 32;

    /// <summary>Failed embed attempts after which a memory row is abandoned; MemorySql's pending selections carry it as the literal 3.</summary>
    internal const int MaxEmbedAttempts = 3;

    private const string EngineProbeText = "ai-raccoon embedding engine probe";

    private const int SqliteBusy = 5;

    private const int SqliteLocked = 6;

    private const string BundledModel = "bundled";

    /// <summary>The open migration (its started_at) whose blank-provider state already produced
    /// the 1012 Warning — one per process per migration, so the 15s relay poll cannot flood (M8).</summary>
    private long? _warnedNoProviderMigration;

    /// <summary>Spent once this process force-opened its one budget-drift migration: a run whose retryable
    /// skips withhold the <c>embedding.chunkBudget</c> stamp waits for the next server start to retry (P1c).</summary>
    private bool _budgetDriftAttempted;

    /// <summary>
    ///     Opens a migration when the engine fingerprint no longer matches the bank's record, or — config-D
    ///     P1c, extending <see cref="IEntryEmbedder.ReconcileFingerprintAsync" />'s contract — force-opens a
    ///     RE-CHUNK-ONLY migration when the <c>embedding.chunkBudget</c> stamp drifts from the resolved budget
    ///     at an equal fingerprint (stale, or absent on a non-empty bank). A zero-row bank is stamped silently.
    ///     False when nothing changed, no engine is recorded, a migration is already open, or this process
    ///     already spent its once-per-server-start drift retry.
    /// </summary>
    public async Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var stored = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Engine, cancellationToken);
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        if (stored is null || string.IsNullOrWhiteSpace(settings.Provider))
        {
            return false;
        }

        var open = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            MemorySql.HasOpenModelMigration, cancellationToken: cancellationToken));
        if (open > 0)
        {
            return false;
        }

        var engine = embeddings.EngineFingerprint(settings.Provider, settings.Model, settings.BaseUrl);
        if (!string.Equals(stored, engine, StringComparison.Ordinal))
        {
            await StartMigrationAsync(connection, settings.Provider, settings.Model, settings.BaseUrl,
                timeProvider.GetUtcNow(), cancellationToken);
            return true;
        }

        return await ReconcileChunkBudgetAsync(connection, settings, engine, cancellationToken);
    }

    /// <summary>
    ///     The budget-drift trigger (P1c): budget drift is an equal-fingerprint case, so
    ///     <see cref="StartMigrationAsync" />'s short-circuit would silently no-op it — this force-opens the
    ///     outbox transaction WITHOUT <c>MarkAllEmbeddedPending</c>: drift moves chunk boundaries, never
    ///     vectors, so unchanged rows keep the vectors they have (review F4).
    /// </summary>
    private async Task<bool> ReconcileChunkBudgetAsync(SqliteConnection connection, EmbeddingSettings settings,
        string engine, CancellationToken cancellationToken)
    {
        if (_budgetDriftAttempted)
        {
            return false;
        }

        var state = await chunkBudgets.CheckAsync(connection, cancellationToken);
        if (state.Matches)
        {
            return false;
        }

        if (state.BankIsEmpty)
        {
            // Nothing to re-chunk and nothing to prove: stamp silently (plan P1c).
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.ChunkBudget,
                state.ResolvedBudget.ToString(CultureInfo.InvariantCulture), cancellationToken);
            return false;
        }

        await OpenMigrationAsync(connection, settings.Provider, settings.Model, settings.BaseUrl, engine,
            timeProvider.GetUtcNow(), markAllEmbeddedPending: false, cancellationToken);
        _budgetDriftAttempted = true;
        return true;
    }

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider,
        string? model, string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previous = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Engine, cancellationToken);
        var engine = embeddings.EngineFingerprint(provider, model, baseUrl);

        if (previous is null || string.Equals(previous, engine, StringComparison.Ordinal))
        {
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Provider, provider, cancellationToken);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Model, model, cancellationToken);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.BaseUrl, baseUrl, cancellationToken);
            await connection.ExecuteAsync(Def(MemorySql.UpsertSetting,
                    new { key = EmbeddingSettingsKeys.Engine, value = engine }, cancellationToken));
            return new EmbeddingConfig(provider, model ?? BundledModel, engine);
        }

        return await OpenMigrationAsync(connection, provider, model, baseUrl, engine, now,
            markAllEmbeddedPending: true, cancellationToken);
    }

    /// <summary>
    ///     The outbox transaction both open paths share: the engine settings, the migration-started row,
    ///     and — only when <paramref name="markAllEmbeddedPending" /> — every embedded row marked pending
    ///     with its embed attempts reset (a re-embed). The budget-drift trigger passes false: its phase
    ///     re-chunks, and replacement rows arrive <c>pending</c> on their own (plan P1c, review F4).
    /// </summary>
    private async Task<EmbeddingConfig> OpenMigrationAsync(SqliteConnection connection, string provider,
        string? model, string? baseUrl, string engine, DateTimeOffset now, bool markAllEmbeddedPending,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Provider, provider, cancellationToken, transaction);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.Model, model, cancellationToken, transaction);
            await UpsertOrDeleteAsync(connection, EmbeddingSettingsKeys.BaseUrl, baseUrl, cancellationToken, transaction);
            await connection.ExecuteAsync(Def(MemorySql.UpsertSetting,
                    new { key = EmbeddingSettingsKeys.Engine, value = engine }, cancellationToken, transaction));

            var started = await connection.ExecuteAsync(Def(MemorySql.StartModelMigration,
                    new { provider, model, baseUrl, engine, startedAt = now.ToUnixTimeSeconds() }, cancellationToken,
                    transaction));
            if (started == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new ModelMigrationInProgressException(
                    "ai-raccoon: a model migration is already in progress; wait for it to finish before starting another");
            }

            if (markAllEmbeddedPending)
            {
                await connection.ExecuteAsync(Def(MemorySql.MarkAllEmbeddedPending, cancellationToken, transaction));
                await connection.ExecuteAsync(Def(MemorySql.ResetEmbedAttempts, cancellationToken, transaction));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (ModelMigrationInProgressException)
        {
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
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
        var preState = await ReadOpenMigrationStateAsync(connection, cancellationToken);

        if (!await migrationLease.TryAcquireAsync(connection, cancellationToken))
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
                Def(MemorySql.HasOpenModelMigration, cancellationToken)) > 0;
            if (!open)
            {
                reporter.MigrationAlreadyFinished(logger, corpus);
                return false;
            }

            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (preState is { } state && state.LeaseWasStale(now))
            {
                reporter.MigrationResumedAfterStall(logger, corpus, state.LeaseOwner!, state.Age(now));
            }

            if (!await HasProviderAsync(connection, cancellationToken))
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

            var owed = await connection.ExecuteScalarAsync<long>(Def(MemorySql.CountPendingEmbed, cancellationToken));
            reporter.MigrationStarted(logger, corpus, owed);

            await ReconcileVecDimensionsAsync(connection, cancellationToken);
            // config-D P1c: the chunk-budget rebudget phase runs HERE — after the vec-dimension reconcile,
            // before the first SelectAllPendingForEmbed — so every vector the loop writes was computed from
            // post-re-chunk values, replacement rows arrive pending and embed in this same drain, and the
            // migration cannot close with rows pending. The report is the phase report (plan P1: its skip
            // counts are what gate the embedding.chunkBudget stamp, which RunAsync writes only at zero
            // retryable skips). Its wall-clock duration and replaced-group count go to the reporter as
            // chunk.rechunk.* (#809) — the phase report is the only place they exist.
            var rechunk = await chunkBudgets.RunAsync(connection, cancellationToken);
            reporter.RechunkFinished(rechunk.Elapsed, rechunk.NoteGroupsRechunked + rechunk.MirrorGroupsRechunked);

            // Time-strided, NOT per-batch: a per-batch line floods (1,492 lines on the owner's
            // 47,723-row backlog) and the metric buffer would drop records. One 1013 per lease
            // TTL crossed — O(elapsed time), and a drain that renews is a drain that reports.
            var nextReport = timeProvider.GetUtcNow() + EmbedDrainReporter.ProgressStride;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = (await connection.QueryAsync<EmbedRow>(Def(MemorySql.SelectAllPendingForEmbed,
                    new { limit = BatchSize }, cancellationToken))).ToList();
                if (batch.Count == 0)
                {
                    break;
                }

                drained += await EmbedAsync(connection, batch, cancellationToken);
                await migrationLease.TryRenewAsync(connection, cancellationToken);
                if (timeProvider.GetUtcNow() >= nextReport)
                {
                    reporter.MigrationProgress(logger, corpus, drained, timeProvider.GetElapsedTime(startedAt));
                    nextReport = timeProvider.GetUtcNow() + EmbedDrainReporter.ProgressStride;
                }
            }

            await connection.ExecuteAsync(Def(MemorySql.FinishModelMigration,
                    new { finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() }, cancellationToken));
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
            await migrationLease.ReleaseAsync(connection, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return;
        }

        await vecDimensions.ReconcileMemoryAsync(connection, embeddings.ResolveDimensions(settings), cancellationToken);
    }

    /// <summary>Embeds one row when an engine is configured; a bank with no engine is left pending.</summary>
    public async Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
        CancellationToken cancellationToken)
    {
        var provider = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return;
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var generator = embeddings.CreateGenerator(settings);
        var headingPath = HeadingPathParser.Parse(value);

        var result = headingPath.Length > 0
            ? await generator.GenerateAsync([embeddings.DocumentText(settings, value), embeddings.DocumentText(settings, headingPath)],
                    cancellationToken: cancellationToken)
            : await generator.GenerateAsync([embeddings.DocumentText(settings, value)], cancellationToken: cancellationToken);
        var structureEmbedding = headingPath.Length > 0 ? EmbeddingBlob.ToBytes(result[1].Vector) : null;

        await connection.ExecuteAsync(Def(MemorySql.MarkEmbedded,
                new
                {
                    id,
                    embedding = EmbeddingBlob.ToBytes(result[0].Vector),
                    headingPath,
                    structureEmbedding
                }, cancellationToken));
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
                    new { projectId, limit = Math.Min(BatchSize, remaining) }, cancellationToken))).ToList();
            if (batch.Count == 0)
            {
                break;
            }

            processed += await EmbedAsync(connection, batch, cancellationToken);
        }

        var healBudget = (limit ?? int.MaxValue) - processed;
        if (healBudget > 0)
        {
            await HealStructureAsync(connection, projectId, healBudget, cancellationToken);
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
            new { limit }, cancellationToken))).ToList();
        return await EmbedAsync(connection, batch, cancellationToken);
    }

    /// <summary>Embeds a query string, or null when the bank has no engine — search degrades rather than failing.</summary>
    public async Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Provider))
        {
            return QueryVector.Empty;
        }

        var generator = embeddings.CreateGenerator(settings);
        var embedded = embeddings.TrimQueryToWindow(settings, query);
        var embedding = await generator.GenerateAsync([embedded], cancellationToken: cancellationToken);
        return new QueryVector(EmbeddingBlob.ToBytes(embedding[0].Vector)) { RelevanceFloor = embeddings.RelevanceFloor(settings) };
    }

    public async Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        new(await connection.ReadSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken) ?? "",
            await connection.ReadSettingAsync(EmbeddingSettingsKeys.Model, cancellationToken),
            await connection.ReadSettingAsync(EmbeddingSettingsKeys.BaseUrl, cancellationToken),
            await connection.ReadSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken),
            int.TryParse(
                await connection.ReadSettingAsync(EmbeddingSettingsKeys.Dimensions, cancellationToken),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions)
                ? dimensions
                : null);

    /// <summary>
    ///     Embeds a set of rows with the configured engine; missing rows are skipped. A sub-batch
    ///     that fails falls back to one row at a time, so one bad row cannot hold the rest hostage.
    /// </summary>
    private async Task<int> EmbedAsync(SqliteConnection connection, IReadOnlyList<EmbedRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var generator = embeddings.CreateGenerator(settings);
        var affected = 0;
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            var batch = rows.Skip(offset).Take(BatchSize).ToList();
            try
            {
                affected += await EmbedBatchAsync(connection, generator, settings, batch, cancellationToken);
            }
            catch (Exception ex) when (IsRowFailure(ex))
            {
                Log.BatchFellBackToOneRowAtATime(logger, batch.Count, ex);
                affected += await EmbedRowByRowAsync(connection, generator, settings, batch, cancellationToken);
            }
        }

        return affected;
    }

    /// <summary>
    ///     Embeds each row on its own and charges a failed attempt to the row that failed. The first
    ///     failure probes the engine: if the engine cannot answer at all, the pass fails and no row
    ///     is charged, so an outage never abandons a healthy backlog.
    /// </summary>
    private async Task<int> EmbedRowByRowAsync(SqliteConnection connection,
        IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingSettings settings,
        IReadOnlyList<EmbedRow> batch, CancellationToken cancellationToken)
    {
        var affected = 0;
        var engineAnswers = false;
        foreach (var row in batch)
        {
            try
            {
                affected += await EmbedBatchAsync(connection, generator, settings, [row], cancellationToken);
                engineAnswers = true;
            }
            catch (Exception ex) when (IsRowFailure(ex))
            {
                if (!engineAnswers)
                {
                    engineAnswers = await EngineAnswersAsync(generator, settings, cancellationToken);
                    if (!engineAnswers)
                    {
                        ExceptionDispatchInfo.Throw(ex);
                    }
                }

                await CountFailedAttemptAsync(connection, row, ex, cancellationToken);
            }
        }

        return affected;
    }

    private async Task<bool> EngineAnswersAsync(IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await generator.GenerateAsync([embeddings.DocumentText(settings, EngineProbeText)],
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CountFailedAttemptAsync(SqliteConnection connection, EmbedRow row, Exception error,
        CancellationToken cancellationToken)
    {
        var attempts = await connection.ExecuteScalarAsync<int>(Def(MemorySql.IncrementEmbedAttempts,
                new { id = row.Id }, cancellationToken));
        Log.RowEmbedAttemptFailed(logger, row.Id, row.Source, attempts, MaxEmbedAttempts, error);
        if (attempts >= MaxEmbedAttempts)
        {
            Log.RowAbandonedAfterMaxEmbedAttempts(logger, row.Id, row.Source, attempts);
        }
    }

    /// <summary>A failure one row can cause: not cancellation, and not another writer holding the bank's lock.</summary>
    private static bool IsRowFailure(Exception ex) =>
        ex is not OperationCanceledException and not SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked };

    /// <summary>
    ///     One generator call for the rows and one for their distinct headings, then every
    ///     <c>MarkEmbedded</c> in a single <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c>; inference stays
    ///     outside the write lock.
    /// </summary>
    private async Task<int> EmbedBatchAsync(SqliteConnection connection,
        IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingSettings settings,
        IReadOnlyList<EmbedRow> batch, CancellationToken cancellationToken)
    {
        var result = await generator.GenerateAsync(batch.Select(r => embeddings.DocumentText(settings, r.Value)),
            cancellationToken: cancellationToken);

        var headingPaths = batch.Select(r => HeadingPathParser.Parse(r.Value)).ToList();
        var structure = await EmbedDistinctHeadingsAsync(generator, headingPaths, path => embeddings.DocumentText(settings, path),
            cancellationToken);
        var embeddingBlobs = result.Select(r => EmbeddingBlob.ToBytes(r.Vector)).ToList();

        var affected = 0;
        await connection.InWriteTransactionAsync(async () =>
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
                        cancellationToken));
            }
        }, cancellationToken);

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
                    new { projectId, limit = Math.Min(BatchSize, remaining) }, cancellationToken))).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var settings = await ReadSettingsAsync(connection, cancellationToken);
            var generator = embeddings.CreateGenerator(settings);
            var headingPaths = candidates.Select(r => HeadingPathParser.Parse(r.Value)).ToList();
            Dictionary<string, byte[]> structure;
            HashSet<string> unembeddable = [];
            try
            {
                structure = await EmbedDistinctHeadingsAsync(generator, headingPaths,
                    path => embeddings.DocumentText(settings, path), cancellationToken);
            }
            catch (Exception ex) when (IsRowFailure(ex))
            {
                Log.BatchFellBackToOneRowAtATime(logger, candidates.Count, ex);
                (structure, unembeddable) = await EmbedHeadingsOneAtATimeAsync(generator, settings, headingPaths,
                    cancellationToken);
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var headingPath = headingPaths[i];
                if (unembeddable.Contains(headingPath))
                {
                    Log.HeadingCannotEmbed(logger, candidates[i].Id, candidates[i].Source);
                    headingPath = "";
                }

                structure.TryGetValue(headingPath, out var structureEmbedding);
                await connection.ExecuteAsync(Def(MemorySql.MarkStructure,
                        new { id = candidates[i].Id, headingPath, structureEmbedding }, cancellationToken));
            }

            remaining -= candidates.Count;
        }
    }

    /// <summary>
    ///     Embeds each distinct heading on its own after a failed batch call, returning the vectors
    ///     and the headings that fail alone while the engine still answers; an engine that cannot
    ///     answer at all rethrows, so no heading is given up on during an outage.
    /// </summary>
    private async Task<(Dictionary<string, byte[]> Vectors, HashSet<string> Unembeddable)> EmbedHeadingsOneAtATimeAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingSettings settings,
        IReadOnlyList<string> headingPaths, CancellationToken cancellationToken)
    {
        var vectors = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var unembeddable = new HashSet<string>(StringComparer.Ordinal);
        var engineAnswers = false;
        foreach (var path in headingPaths.Where(path => path.Length > 0).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var result = await generator.GenerateAsync([embeddings.DocumentText(settings, path)],
                    cancellationToken: cancellationToken);
                vectors[path] = EmbeddingBlob.ToBytes(result[0].Vector);
                engineAnswers = true;
            }
            catch (Exception ex) when (IsRowFailure(ex))
            {
                if (!engineAnswers)
                {
                    engineAnswers = await EngineAnswersAsync(generator, settings, cancellationToken);
                    if (!engineAnswers)
                    {
                        ExceptionDispatchInfo.Throw(ex);
                    }
                }

                unembeddable.Add(path);
            }
        }

        return (vectors, unembeddable);
    }

    /// <summary>Embeds each distinct non-empty heading path once; empty paths are omitted from the result.</summary>
    private static async Task<Dictionary<string, byte[]>> EmbedDistinctHeadingsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<string> headingPaths,
        Func<string, string> documentText, CancellationToken cancellationToken)
    {
        var distinct = headingPaths.Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var result = await generator.GenerateAsync(distinct.Select(documentText), cancellationToken: cancellationToken);
        var vectors = new Dictionary<string, byte[]>(distinct.Count, StringComparer.Ordinal);
        for (var i = 0; i < distinct.Count; i++)
        {
            vectors[distinct[i]] = EmbeddingBlob.ToBytes(result[i].Vector);
        }

        return vectors;
    }


    private static async Task<bool> HasProviderAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var provider = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken);
        return !string.IsNullOrWhiteSpace(provider);
    }

    private static async Task<OpenMigrationState?> ReadOpenMigrationStateAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<OpenMigrationState>(
            Def(MemorySql.SelectOpenModelMigrationLease, cancellationToken));

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
            : Def(MemorySql.UpsertSetting, new { key, value }, cancellationToken, transaction));


    internal sealed record EmbedRow
    {
        public long Id { get; init; }

        public string Value { get; init; } = "";

        /// <summary>The row's source file, or its path when it has none: what a reader needs to find it.</summary>
        public string? Source { get; init; }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 442, Level = LogLevel.Debug,
            Message = "A batch of {Rows} memory rows failed to embed as one call; retrying them one at a time")]
        public static partial void BatchFellBackToOneRowAtATime(ILogger logger, int rows, Exception exception);

        [LoggerMessage(EventId = 443, Level = LogLevel.Warning,
            Message = "Memory row {RowId} ({Source}) failed to embed on attempt {Attempts} of {MaxAttempts}; "
                      + "it stays pending and the drain will retry it")]
        public static partial void RowEmbedAttemptFailed(ILogger logger, long rowId, string? source, int attempts,
            int maxAttempts, Exception exception);

        [LoggerMessage(EventId = 444, Level = LogLevel.Error,
            Message = "Giving up on memory row {RowId} ({Source}) after {Attempts} failed embed attempts: it will not "
                      + "be selected for embedding again and search cannot match it on meaning. Rewrite or re-ingest "
                      + "it once the cause is gone; switching the embedding model also retries it.")]
        public static partial void RowAbandonedAfterMaxEmbedAttempts(ILogger logger, long rowId, string? source,
            int attempts);

        [LoggerMessage(EventId = 445, Level = LogLevel.Warning,
            Message = "The heading of memory row {RowId} ({Source}) cannot embed; the row keeps its content vector "
                      + "and goes without a structure vector")]
        public static partial void HeadingCannotEmbed(ILogger logger, long rowId, string? source);
    }
}
