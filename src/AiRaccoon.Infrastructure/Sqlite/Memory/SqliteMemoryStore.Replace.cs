using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>Replace-by-path: the watch digest's fingerprint-gated <see cref="ReplaceIfFileChangedAsync" />
/// and a repair's unconditional <see cref="ReplaceAsync" />, both over one shared transactional core.</summary>
public sealed partial class SqliteMemoryStore
{
    /// <summary>
    ///     Watch-digest replace-by-path: skips when the stored fingerprint already equals
    ///     <paramref name="fileHash" />, else runs the same replace <see cref="ReplaceAsync" /> does.
    ///     The check runs under <see cref="ReplaceCoreAsync" />'s own write lock (its
    ///     <paramref name="fileHash" />-comparing <c>guard</c>), not a separate connection — two
    ///     digests racing the same stale-to-new transition still chunk and embed exactly once.
    /// </summary>
    public Task<ReplaceResult> ReplaceIfFileChangedAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);

        return ReplaceIfChangedCoreAsync(projectId, path, fileHash,
            guard: async connection =>
            {
                var stored = await connection.ExecuteScalarAsync<string?>(
                        Def(MemorySql.SelectWatchFile, new { projectId, path }, cancellationToken))
                    .ConfigureAwait(false);
                return !string.Equals(stored, fileHash, StringComparison.Ordinal);
            }, cancellationToken);
    }

    /// <summary>
    ///     Unconditional replace-by-path: runs <see cref="ReplaceCoreAsync" /> regardless of the
    ///     stored fingerprint — for a repair re-chunking a file whose content has not changed.
    /// </summary>
    public async Task ReplaceAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);

        await ReplaceIfChangedCoreAsync(projectId, path, fileHash, guard: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Direct-tool replace-by-path (Defect B): the same delete-then-ingest transaction the watch
    ///     digest runs, without the <c>watch_files</c> fingerprint — a direct ingest of a file nobody
    ///     watches has no business claiming one. Returns the rows the re-ingest wrote.
    /// </summary>
    /// <summary>
    ///     Direct-tool ingest that replaces the path's chunk set (Defect B). Ingest runs first and
    ///     reports the chunks it wrote or rediscovered unchanged; anything else still stored for the
    ///     path is a leftover of a previous chunking and is pruned.
    ///     <para>
    ///         Ingest-then-prune, not delete-then-ingest: a blanket delete rewrites every row of an
    ///         UNCHANGED file and re-queues its embeddings, and `memory_ingest_directory` walks per
    ///         file, so a no-op directory re-ingest would demote the whole tree in hybrid search
    ///         until it drained.
    ///     </para>
    ///     <para>
    ///         The ingest leaves its row(s) `embed_state = 'pending'` and deliberately does NOT run
    ///         inside the prune's transaction — each insert autocommits, so by the time this method
    ///         enqueues the embed-drain signal below, the rows it is signalling for already exist.
    ///         Only the prune takes the write lock, and only for one DELETE. A crash between the two
    ///         leaves the stale rows exactly where today's code leaves them, so the window costs
    ///         nothing that is not already the status quo.
    ///     </para>
    ///     No watch fingerprint is written: a direct ingest of a file nobody watches must not claim
    ///     one.
    /// </summary>
    internal async Task<int> ReplaceForDirectIngestAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var ingestResult = await fileIngestor
            .IngestFileAsync(connection, projectId, path, context, cancellationToken)
            .ConfigureAwait(false);

        await PruneChunksNotIn(connection, projectId, path, ingestResult.ChunkHashes ?? [],
                ingestResult.CodeChunkHashes, cancellationToken)
            .ConfigureAwait(false);

        embedDrainPump.SignalWritten(ingestResult.WrittenCorpus);
        return ingestResult.RowsInserted;
    }

    /// <summary>
    ///     Deletes what the ingest did not account for, holding the write lock only for that (its own
    ///     short <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c> around <see cref="PruneAsync" />) — the shape
    ///     every caller but <see cref="ReplaceCoreAsync" /> wants. <see cref="ReplaceCoreAsync" />
    ///     instead calls <see cref="PruneAsync" /> directly, inside its own already-open transaction,
    ///     since it has more to do (guard re-check, fingerprint) under the same lock.
    /// </summary>
    private async Task PruneChunksNotIn(SqliteConnection connection, string projectId, string path,
        IReadOnlyList<string> keep, IReadOnlyList<string>? keepCode, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await PruneAsync(connection, projectId, path, keep, keepCode, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     Deletes what the ingest did not account for. The caller holds the write lock for this
    ///     (and whatever else shares its transaction) — this method issues no BEGIN/COMMIT of its
    ///     own. The embed-queue capture/restore comes along because the delete fires
    ///     <c>promotion_queue_entries_ad</c> (ADR-0023); <c>RestoreQueueRowsStillBacked</c> restores
    ///     only rows a surviving entry still backs, so a pruned chunk's queue row correctly goes
    ///     with it. `code_entries` has no promotion-queue trigger, so its leg needs no such dance.
    ///     <paramref name="keepCode" /> null (#436) means the ingest has no trustworthy code-corpus
    ///     chunk set to prune by — never a stand-in chunker's zero chunks read as "delete
    ///     everything" — so the code leg is skipped entirely rather than run with an empty keep list.
    ///     <para>
    ///         WP12 review finding: a KEPT hash (still in <paramref name="keep" />) is never deleted
    ///         from `entries`, so the cascade above never fires for it — a discarded hash whose row
    ///         survives a replace (unchanged content) would otherwise keep its stale
    ///         `promotion_queue` row forever. <see cref="MemorySql.DeleteDiscardedQueueRowsForSourcePath" />
    ///         sweeps that residue explicitly, scoped to this path.
    ///     </para>
    /// </summary>
    private async Task PruneAsync(SqliteConnection connection, string projectId, string path,
        IReadOnlyList<string> keep, IReadOnlyList<string>? keepCode, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(Def(MemorySql.CreateQueueRestoreTable, null, cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                Def(MemorySql.CaptureQueueRowsForSourcePath,
                    new { projectId, path, pathPrefix = LikePattern.Escape(path) + "/%" }, cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                Def(MemorySql.DeleteDiscardedQueueRowsForSourcePath,
                    new { projectId, path, pathPrefix = LikePattern.Escape(path) + "/%" }, cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(keep.Count == 0
                ? Def(MemorySql.DeleteAllChunksForPath, new { projectId, path }, cancellationToken)
                : Def(MemorySql.DeleteChunksForPathExcept, new { projectId, path, keep }, cancellationToken))
            .ConfigureAwait(false);
        if (keepCode is not null)
        {
            var codeParams = new
            {
                projectId, path, pathPrefix = LikePattern.Escape(path) + "/%", keep = keepCode
            };
            await connection.ExecuteAsync(keepCode.Count == 0
                    ? Def(MemorySql.DeleteAllCodeChunksForPath, codeParams, cancellationToken)
                    : Def(MemorySql.DeleteCodeChunksForPathExcept, codeParams, cancellationToken))
                .ConfigureAwait(false);
        }

        await connection.ExecuteAsync(Def(MemorySql.RestoreQueueRowsStillBacked, null, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<ReplaceResult> ReplaceIfChangedCoreAsync(string projectId, string path, string fileHash,
        Func<SqliteConnection, Task<bool>>? guard, CancellationToken cancellationToken)
    {
        var result = await ReplaceCoreAsync(projectId, path, fileHash, guard, context: null,
            fingerprint: true, cancellationToken).ConfigureAwait(false);
        return new ReplaceResult(result.Ran, result.Corpus);
    }

    /// <summary>A stale claim (its holder crashed mid-chunk and never released it) can be reclaimed after this long.</summary>
    private static readonly TimeSpan ClaimStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Replace-by-path (WP12 Fix A): chunking runs OUTSIDE the write lock, exactly like
    ///     <see cref="ReplaceForDirectIngestAsync" /> — a cheap unlocked <paramref name="guard" />
    ///     read decides whether to bother at all, then (when a <paramref name="guard" /> exists,
    ///     i.e. the watch digest, never the unconditional repair) a short claim transaction decides
    ///     which of two racing replaces on the same path gets to chunk it —
    ///     <see cref="MemorySql.TryClaimWatchDigest" />; the fingerprint alone cannot gate this,
    ///     since it is not written until everything else is done. The loser declines without ever
    ///     calling the chunker. The claim's winner runs
    ///     <see cref="IFileIngestor.IngestFileAsync" /> autocommit (file read, chunk, hash, insert;
    ///     rows left `embed_state = 'pending'`), then a short prune-and-fingerprint transaction:
    ///     the delete of whatever the ingest did not account for, the fingerprint write, and
    ///     releasing the claim.
    ///     <para>
    ///         Traded against the old delete-then-ingest shape: a reader can see the file's old AND
    ///         new chunks during the short window between the ingest's autocommit and the prune's
    ///         commit, instead of never seeing it chunkless — the same trade
    ///         <see cref="ReplaceForDirectIngestAsync" />'s own doc comment already made. A crash or a
    ///         forced-failure mid-prune leaves both old and new chunks in place rather than rolling
    ///         the ingest back too, since the ingest is no longer part of that transaction.
    ///     </para>
    ///     The guard is each caller's own decision (<see cref="ReplaceIfFileChangedAsync" /> passes
    ///     one, <see cref="ReplaceAsync" /> passes none); this method knows nothing about
    ///     fingerprints.
    /// </summary>
    private async Task<ReplaceCoreResult> ReplaceCoreAsync(string projectId, string path,
        string? fileHash, Func<SqliteConnection, Task<bool>>? guard, string? context, bool fingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Unlocked pre-check: the common case (an unrelated concurrent replace never raced this
        // path) declines here without ever touching the write lock.
        if (guard is not null && !await guard(connection).ConfigureAwait(false))
        {
            return new ReplaceCoreResult(false, 0, CorpusKind.Neither);
        }

        if (guard is not null)
        {
            var claimed = await TryClaimChunkerAsync(connection, projectId, path, guard, cancellationToken)
                .ConfigureAwait(false);
            if (!claimed)
            {
                // Another replace on this exact path already owns the chunk — its own commit
                // (fingerprint + released claim) will make the target state durable. Chunking here
                // too would defeat the claim's whole point: chunk this path exactly once.
                return new ReplaceCoreResult(false, 0, CorpusKind.Neither);
            }
        }

        try
        {
            // fileIngestor self-filters by extension and, when the path is a code file, dispatches to
            // ICodeIngestor internally (FileIngestor.IngestFileAsync) — one call covers both corpora.
            // Autocommit, outside any lock: this is the chunker the write lock used to be held through.
            var ingestResult = await fileIngestor
                .IngestFileAsync(connection, projectId, path, context, cancellationToken)
                .ConfigureAwait(false);

            var waitFrom = timeProvider.GetTimestamp();
            await connection.ExecuteAsync(
                    new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var waitMs = timeProvider.GetElapsedTime(waitFrom).TotalMilliseconds;
            var heldFrom = timeProvider.GetTimestamp();
            try
            {
                await PruneAsync(connection, projectId, path, ingestResult.ChunkHashes ?? [],
                        ingestResult.CodeChunkHashes, cancellationToken)
                    .ConfigureAwait(false);
                if (guard is not null)
                {
                    await connection.ExecuteAsync(Def(MemorySql.ReleaseWatchDigestClaim,
                            new { projectId, path }, cancellationToken))
                        .ConfigureAwait(false);
                }

                // B1: a code-only file a stand-in chunker (e.g. NoOpCodeChunker) produced zero rows
                // for must NOT be fingerprinted — a fingerprint here would hash-skip it forever, even
                // past the point a real chunker starts producing rows for unchanged content. Every
                // other outcome (memory match, mixed match, no corpus matches at all) fingerprints
                // as before.
                if (fingerprint && ingestResult.FingerprintEligible)
                {
                    await connection.ExecuteAsync(
                            Def(MemorySql.UpsertWatchFile,
                                new
                                {
                                    projectId, path, fileHash,
                                    updatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
                                }, cancellationToken))
                        .ConfigureAwait(false);
                }

                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var heldMs = timeProvider.GetElapsedTime(heldFrom).TotalMilliseconds;
                Log.TransactionHeld(logger, waitMs, heldMs, ingestResult.RowsInserted);
                RecordReplaceLockMetrics(projectId, waitMs, heldMs, ingestResult.RowsInserted);
                return new ReplaceCoreResult(true, ingestResult.RowsInserted, ingestResult.WrittenCorpus);
            }
            catch
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                throw;
            }
        }
        catch when (guard is not null)
        {
            // The claim must never outlive a failed chunk/prune — otherwise every later replace of
            // this path declines forever, thinking someone else still owns it (ClaimStaleAfter is
            // the crash-only backstop for this same release, not the fast path).
            await using var release = await factory.OpenBankAsync(CancellationToken.None).ConfigureAwait(false);
            await release.ExecuteAsync(Def(MemorySql.ReleaseWatchDigestClaim, new { projectId, path },
                    CancellationToken.None))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     A short <c>BEGIN IMMEDIATE</c> that re-checks <paramref name="guard" /> authoritatively
    ///     (a racer that already committed while this call waited for the lock) and, if still
    ///     stale, claims the chunker for <paramref name="path" /> — <see cref="MemorySql.TryClaimWatchDigest" />'s
    ///     affected-row count says whether this call now owns it. Either way the lock is released
    ///     immediately after; the chunker itself never runs under it.
    /// </summary>
    private async Task<bool> TryClaimChunkerAsync(SqliteConnection connection, string projectId, string path,
        Func<SqliteConnection, Task<bool>> guard, CancellationToken cancellationToken)
    {
        var waitFrom = timeProvider.GetTimestamp();
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var waitMs = timeProvider.GetElapsedTime(waitFrom).TotalMilliseconds;
        var heldFrom = timeProvider.GetTimestamp();
        try
        {
            if (!await guard(connection).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var declinedHeldMs = timeProvider.GetElapsedTime(heldFrom).TotalMilliseconds;
                Log.TransactionHeld(logger, waitMs, declinedHeldMs, 0);
                RecordReplaceLockMetrics(projectId, waitMs, declinedHeldMs, 0);
                return false;
            }

            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var claimed = await connection.ExecuteAsync(
                    Def(MemorySql.TryClaimWatchDigest,
                        new { projectId, path, claimedAt = now, staleAfterSeconds = (long)ClaimStaleAfter.TotalSeconds },
                        cancellationToken))
                .ConfigureAwait(false) > 0;
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (!claimed)
            {
                var heldMs = timeProvider.GetElapsedTime(heldFrom).TotalMilliseconds;
                Log.TransactionHeld(logger, waitMs, heldMs, 0);
                RecordReplaceLockMetrics(projectId, waitMs, heldMs, 0);
            }

            return claimed;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     WP11 (log-values-as-metrics) + WP12 (wait/held split): EventId 899's wait/held ms and row
    ///     count, recorded beside the log line above — same values, not a second computation. Under
    ///     the WRITING project's own id: unlike a drain pass, a replace transaction always has one.
    /// </summary>
    private void RecordReplaceLockMetrics(string projectId, double waitMs, double heldMs, int rows)
    {
        var now = timeProvider.GetUtcNow();
        measurements.Record(new Measurement(MetricsConfigKeys.ReplaceWaitMsMetricName,
            MeasurementKind.Histogram, waitMs, "ms", now, projectId));
        measurements.Record(new Measurement(MetricsConfigKeys.ReplaceHeldMsMetricName,
            MeasurementKind.Histogram, heldMs, "ms", now, projectId));
        measurements.Record(new Measurement(MetricsConfigKeys.ReplaceRowsMetricName,
            MeasurementKind.Histogram, rows, "count", now, projectId));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 899, Level = LogLevel.Information,
            Message = "Replace-by-path waited {WaitMs:F1} ms for the write lock and held it {HeldMs:F1} ms "
                      + "({Rows} row(s) written)")]
        public static partial void TransactionHeld(ILogger logger, double waitMs, double heldMs, int rows);
    }

    /// <summary><see cref="ReplaceCoreAsync" />'s outcome: whether it ran at all, the rows it wrote, and which corpus.</summary>
    private readonly record struct ReplaceCoreResult(bool Ran, int Rows, CorpusKind Corpus);
}
