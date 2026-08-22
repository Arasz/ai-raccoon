using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
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

        EnqueueDrainFor(ingestResult.WrittenCorpus);
        return ingestResult.RowsInserted;
    }

    /// <summary>The one place a written <see cref="CorpusKind" /> becomes an embed-topic signal —
    /// <see cref="CorpusKind.Neither" /> enqueues nothing.</summary>
    private void EnqueueDrainFor(CorpusKind corpus)
    {
        switch (corpus)
        {
            case CorpusKind.Memory:
                embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
                break;
            case CorpusKind.Code:
                embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code));
                break;
            case CorpusKind.Neither:
                break;
        }
    }

    /// <summary>
    ///     Deletes what the ingest did not account for, holding the write lock only for that. The
    ///     embed-queue capture/restore comes along because the delete fires
    ///     <c>promotion_queue_entries_ad</c> (ADR-0023); <c>RestoreQueueRowsStillBacked</c> restores
    ///     only rows a surviving entry still backs, so a pruned chunk's queue row correctly goes
    ///     with it. `code_entries` has no promotion-queue trigger, so its leg needs no such dance.
    ///     <paramref name="keepCode" /> null (#436) means the ingest has no trustworthy code-corpus
    ///     chunk set to prune by — never a stand-in chunker's zero chunks read as "delete
    ///     everything" — so the code leg is skipped entirely rather than run with an empty keep list.
    /// </summary>
    private async Task PruneChunksNotIn(SqliteConnection connection, string projectId, string path,
        IReadOnlyList<string> keep, IReadOnlyList<string>? keepCode, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(Def(MemorySql.CreateQueueRestoreTable, null, cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.CaptureQueueRowsForSourcePath,
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

    private async Task<ReplaceResult> ReplaceIfChangedCoreAsync(string projectId, string path, string fileHash,
        Func<SqliteConnection, Task<bool>>? guard, CancellationToken cancellationToken)
    {
        var (ran, _, corpus) = await ReplaceCoreAsync(projectId, path, fileHash, guard, context: null,
            fingerprint: true, cancellationToken).ConfigureAwait(false);
        return new ReplaceResult(ran, corpus);
    }

    /// <summary>
    ///     Replace-by-path, all in one transaction so no reader ever sees the file chunkless and a
    ///     crash rolls the whole replace back: an optional <paramref name="guard" /> decides whether
    ///     to proceed at all (evaluated after the write lock is held, so it is authoritative), then
    ///     delete, re-ingest, fingerprint write. Embedding is left pending for the caller to run after
    ///     the commit — the engine is far too slow to hold the bank's write lock through. The guard is
    ///     each caller's own decision (<see cref="ReplaceIfFileChangedAsync" /> passes one,
    ///     <see cref="ReplaceAsync" /> passes none); this method knows nothing about fingerprints.
    /// </summary>
    private async Task<(bool Ran, int Rows, CorpusKind Corpus)> ReplaceCoreAsync(string projectId, string path,
        string? fileHash, Func<SqliteConnection, Task<bool>>? guard, string? context, bool fingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Logs how long the write lock was held — a measurement, never a threshold assertion.
        var heldFrom = timeProvider.GetTimestamp();
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            if (guard is not null && !await guard(connection).ConfigureAwait(false))
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                Log.TransactionHeld(logger, timeProvider.GetElapsedTime(heldFrom).TotalMilliseconds, 0);
                return (false, 0, CorpusKind.Neither);
            }

            var pathPrefix = LikePattern.Escape(path) + "/%";
            await connection.ExecuteAsync(
                    Def(MemorySql.CreateQueueRestoreTable, null, cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.CaptureQueueRowsForSourcePath, new { projectId, path, pathPrefix },
                        cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.DeleteBySourcePath, new { projectId, path, pathPrefix }, cancellationToken))
                .ConfigureAwait(false);
            // Code corpus leg (docs/work/2026-08-21-code-search-implementation-plan.md §3.5):
            // unconditional, same reasoning as DeleteSourcePathAsync above.
            await connection.ExecuteAsync(
                    Def(MemorySql.DeleteCodeBySourcePath, new { projectId, path, pathPrefix }, cancellationToken))
                .ConfigureAwait(false);
            // fileIngestor self-filters by extension and, when the path is a code file, dispatches
            // to ICodeIngestor internally (FileIngestor.IngestFileAsync) — one re-ingest call
            // covers both corpora; each ingestor's own matcher decides whether it does anything.
            var ingestResult = await fileIngestor
                .IngestFileAsync(connection, projectId, path, context, cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.RestoreQueueRowsStillBacked, null, cancellationToken))
                .ConfigureAwait(false);
            // B1: a code-only file a stand-in chunker (e.g. NoOpCodeChunker) produced zero rows for
            // must NOT be fingerprinted — a fingerprint here would hash-skip it forever, even past
            // the point a real chunker starts producing rows for unchanged content. Every other
            // outcome (memory match, mixed match, no corpus matches at all) fingerprints as before.
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
            Log.TransactionHeld(logger, timeProvider.GetElapsedTime(heldFrom).TotalMilliseconds, ingestResult.RowsInserted);
            return (true, ingestResult.RowsInserted, ingestResult.WrittenCorpus);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 899, Level = LogLevel.Information,
            Message = "Replace-by-path transaction held the write lock for {ElapsedMs:F1} ms ({Rows} row(s) written)")]
        public static partial void TransactionHeld(ILogger logger, double elapsedMs, int rows);
    }
}
