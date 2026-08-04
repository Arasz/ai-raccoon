using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Row-merge sync over S3-compatible object storage: VACUUM INTO snapshot → pull → ATTACH+merge → push If-Match.</summary>
public partial class SyncService(
    ICloudStore cloud,
    Func<CancellationToken, Task<SqliteConnection>> openBank,
    Func<string, CancellationToken, Task<SqliteConnection>> openReadOnly,
    TimeProvider timeProvider,
    ILogger<SyncService> logger)
{
    private const int MaxPushRetries = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public virtual async Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncCycleAsync(projectId, objectKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> SyncCycleAsync(string projectId, string objectKey,
        CancellationToken cancellationToken)
    {
        // 1. VACUUM INTO a temp snapshot of the current local bank.
        var localSnapshot = Path.GetTempFileName();
        try
        {
            await using (var conn = await openBank(cancellationToken).ConfigureAwait(false))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{localSnapshot}'";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Strip workspace rows — they never leave the bank.
            await using (var snap = new SqliteConnection($"Data Source={localSnapshot}"))
            {
                await snap.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var del = snap.CreateCommand();
                del.CommandText = "DELETE FROM entries WHERE workspace_id IS NOT NULL";
                await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await using var vac = snap.CreateCommand();
                vac.CommandText = "VACUUM";
                await vac.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2. Integrity check on the snapshot.
            await using (var ro = await openReadOnly(localSnapshot, cancellationToken).ConfigureAwait(false))
            {
                await using var integrity = ro.CreateCommand();
                integrity.CommandText = "PRAGMA quick_check";
                var result = (string)(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncCorruptFileException($"Local snapshot integrity check failed: {result}");
                }
            }

            var snapshotBytes = await File.ReadAllBytesAsync(localSnapshot, cancellationToken).ConfigureAwait(false);

            // 3. Pull the remote snapshot.
            var remote = await cloud.PullAsync(objectKey, cancellationToken).ConfigureAwait(false);

            var sent = 0;
            var received = 0;
            var reindexed = 0;
            var remoteETag = remote?.ETag;

            if (remote is not null)
            {
                // Remote exists — ATTACH and merge.
                (received, reindexed) = await MergeRemoteAsync(projectId, remote.Data, cancellationToken).ConfigureAwait(false);
                // Re-read the merged local bank as the new snapshot.
                var mergedPath = Path.GetTempFileName();
                try
                {
                    await WaitForWalCheckpointAsync(cancellationToken).ConfigureAwait(false);
                    await using (var conn = await openBank(cancellationToken).ConfigureAwait(false))
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"VACUUM INTO '{mergedPath}'";
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    snapshotBytes = await File.ReadAllBytesAsync(mergedPath, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (File.Exists(mergedPath))
                    {
                        File.Delete(mergedPath);
                    }
                }
            }

            // 4. Push with If-Match, retrying on 412/conflict.
            var pushETag = remoteETag;
            for (var attempt = 1; attempt <= MaxPushRetries; attempt++)
            {
                try
                {
                    var newETag = await cloud.PushAsync(objectKey, snapshotBytes, pushETag, cancellationToken)
                        .ConfigureAwait(false);

                    // Record the ETag watermark.
                    await using (var conn = await openBank(cancellationToken).ConfigureAwait(false))
                    {
                        await using var upsert = conn.CreateCommand();
                        upsert.CommandText = "INSERT INTO sync_meta (key, value) VALUES ('last_etag', @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                        upsert.Parameters.AddWithValue("@value", newETag);
                        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    sent = 1;
                    break;
                }
                catch (SyncConflictException) when (attempt < MaxPushRetries)
                {
                    // Re-pull and re-merge.
                    remote = await cloud.PullAsync(objectKey, cancellationToken).ConfigureAwait(false);
                    remoteETag = remote?.ETag;
                    pushETag = remoteETag;
                    if (remote is not null)
                    {
                        (received, reindexed) = await MergeRemoteAsync(projectId, remote.Data, cancellationToken)
                            .ConfigureAwait(false);
                        // Re-read merged snapshot.
                        var retryPath = Path.GetTempFileName();
                        try
                        {
                            await WaitForWalCheckpointAsync(cancellationToken).ConfigureAwait(false);
                            await using (var conn = await openBank(cancellationToken).ConfigureAwait(false))
                            {
                                await using var cmd = conn.CreateCommand();
                                cmd.CommandText = $"VACUUM INTO '{retryPath}'";
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                            }

                            snapshotBytes = await File.ReadAllBytesAsync(retryPath, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (File.Exists(retryPath))
                            {
                                File.Delete(retryPath);
                            }
                        }
                    }
                }
                catch (SyncConflictException)
                {
                    Log.SyncConflictExhausted(logger, MaxPushRetries);
                    throw;
                }
            }

            return new SyncResult(sent, received, reindexed);
        }
        finally
        {
            if (File.Exists(localSnapshot))
            {
                File.Delete(localSnapshot);
            }
        }
    }

    private async Task<(int Received, int Reindexed)> MergeRemoteAsync(string projectId, byte[] remoteData,
        CancellationToken cancellationToken)
    {
        var remotePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(remotePath, remoteData, cancellationToken).ConfigureAwait(false);

            // Integrity check the remote snapshot.
            try
            {
                await using (var ro = await openReadOnly(remotePath, cancellationToken).ConfigureAwait(false))
                {
                    await using var check = ro.CreateCommand();
                    check.CommandText = "PRAGMA quick_check";
                    var result = (string)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SyncCorruptFileException($"Remote snapshot integrity check failed: {result}");
                    }
                }
            }
            catch (SqliteException ex)
            {
                throw new SyncCorruptFileException($"Remote snapshot is corrupt: {ex.Message}");
            }

            await using var conn = await openBank(cancellationToken).ConfigureAwait(false);

            // ATTACH the remote snapshot.
            await using var attach = conn.CreateCommand();
            attach.CommandText = $"ATTACH DATABASE '{remotePath}' AS remote";
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var received = 0;

                // Merge entries: content-addressed near-union (skip duplicates).
                await using (var mergeEntries = conn.CreateCommand())
                {
                    mergeEntries.CommandText = """
                                               INSERT OR IGNORE INTO entries (hash, path, value, scope, project_id, context_label,
                                                                               workspace_id, agent_id, created_at, updated_at,
                                                                               access_count, last_accessed_at, rating, ttl_days,
                                                                               embed_state, embedding)
                                               SELECT r.hash, r.path, r.value, r.scope, r.project_id, r.context_label,
                                                      r.workspace_id, r.agent_id, r.created_at, r.updated_at,
                                                      r.access_count, r.last_accessed_at, r.rating, r.ttl_days,
                                                      'pending', NULL
                                               FROM remote.entries r
                                               WHERE r.workspace_id IS NULL
                                                 AND NOT EXISTS (
                                                     SELECT 1 FROM sync_tombstones t
                                                     WHERE t.hash = r.hash AND t.scope = COALESCE(r.scope, 'workspace')
                                                 )
                                               """;
                    received += await mergeEntries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Merge settings: updated_at LWW.
                await using (var mergeSettings = conn.CreateCommand())
                {
                    mergeSettings.CommandText = """
                                                INSERT INTO settings (key, value)
                                                SELECT key, value FROM remote.settings
                                                WHERE true
                                                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                                                """;
                    await mergeSettings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Merge sync_tombstones: union.
                await using (var mergeTombstones = conn.CreateCommand())
                {
                    mergeTombstones.CommandText = """
                                                  INSERT OR IGNORE INTO sync_tombstones (hash, scope, deleted_at)
                                                  SELECT hash, scope, deleted_at FROM remote.sync_tombstones
                                                  """;
                    await mergeTombstones.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Apply tombstones: delete rows locally that remote deleted.
                await using (var applyTombstones = conn.CreateCommand())
                {
                    applyTombstones.CommandText = """
                                                  DELETE FROM entries
                                                  WHERE (hash, COALESCE(scope, 'workspace'))
                                                      IN (SELECT hash, scope FROM remote.sync_tombstones)
                                                  """;
                    await applyTombstones.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // GC tombstones: remove rows older than the last pull watermark.
                await using (var watermarkCmd = conn.CreateCommand())
                {
                    watermarkCmd.CommandText = "SELECT value FROM sync_meta WHERE key = 'last_pull_at'";
                    var lastPullStr = (string?)await watermarkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (long.TryParse(lastPullStr, out var lastPull))
                    {
                        await using var gc = conn.CreateCommand();
                        gc.CommandText = "DELETE FROM sync_tombstones WHERE deleted_at < @watermark";
                        gc.Parameters.AddWithValue("@watermark", lastPull);
                        await gc.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                // Record last pull timestamp.
                var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
                await using (var updateWatermark = conn.CreateCommand())
                {
                    updateWatermark.CommandText =
                        "INSERT INTO sync_meta (key, value) VALUES ('last_pull_at', @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                    updateWatermark.Parameters.AddWithValue("@value", now.ToString());
                    await updateWatermark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Reindex: enqueue new/changed rows into the pending embed queue.
                var reindexed = 0;
                await using (var reindexCmd = conn.CreateCommand())
                {
                    reindexCmd.CommandText = """
                                             UPDATE entries
                                             SET embed_state = 'pending', embedding = NULL
                                             WHERE embed_state = 'embedded'
                                               AND id IN (
                                                   SELECT e.id FROM entries e
                                                   INNER JOIN remote.entries r ON e.hash = r.hash AND COALESCE(e.scope, 'workspace') = COALESCE(r.scope, 'workspace')
                                                   WHERE e.workspace_id IS NULL
                                               )
                                             """;
                    reindexed = await reindexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                return (received, reindexed);
            }
            finally
            {
                await using var detach = conn.CreateCommand();
                detach.CommandText = "DETACH DATABASE remote";
                await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(remotePath))
            {
                File.Delete(remotePath);
            }
        }
    }

    private async Task WaitForWalCheckpointAsync(CancellationToken cancellationToken)
    {
        await using var conn = await openBank(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
            Message = "Sync If-Match push exhausted after {retries} retries")]
        public static partial void SyncConflictExhausted(ILogger logger, int retries);
    }
}
