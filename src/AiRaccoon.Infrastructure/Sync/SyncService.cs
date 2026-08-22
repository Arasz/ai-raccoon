using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Row-merge sync over object storage: VACUUM INTO snapshot → pull → ATTACH+merge → push If-Match.</summary>
public partial class SyncService(
    Func<CancellationToken, Task<ICloudStore>> resolveCloud,
    Func<CancellationToken, Task<SqliteConnection>> openBank,
    Func<string, CancellationToken, Task<SqliteConnection>> openSnapshot,
    Func<string, CancellationToken, Task<SqliteConnection>> openReadOnly,
    TimeProvider timeProvider,
    ILogger<SyncService> logger,
    ISyncBlobAuthenticator? blobAuthenticator = null) : ISyncService
{
    private const int MaxPushRetries = 3;
    private readonly ISyncBlobAuthenticator _authenticator = blobAuthenticator ?? new SyncBlobAuthenticator();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Convenience ctor for a fixed store (tests); the DI path resolves per call.</summary>
    public SyncService(ICloudStore cloud, Func<CancellationToken, Task<SqliteConnection>> openBank,
        Func<string, CancellationToken, Task<SqliteConnection>> openSnapshot,
        Func<string, CancellationToken, Task<SqliteConnection>> openReadOnly,
        TimeProvider timeProvider, ILogger<SyncService> logger)
        : this(_ => Task.FromResult(cloud), openBank, openSnapshot, openReadOnly, timeProvider, logger)
    {
    }

    /// <summary>Defaults <paramref name="objectKey" /> to "memory-{projectId}.db" when the caller has none configured — the naming convention is the service's concern, not the caller's.</summary>
    public virtual async Task<SyncResult> MemorySyncAsync(string projectId, string? objectKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var key = string.IsNullOrWhiteSpace(objectKey) ? $"memory-{projectId}.db" : objectKey;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncCycleAsync(projectId, key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> SyncCycleAsync(string projectId, string objectKey,
        CancellationToken cancellationToken)
    {
        // The cloud store is resolved per call from the current settings rows — `sync add/remove`
        // take effect without a restart.
        var cloud = await resolveCloud(cancellationToken).ConfigureAwait(false);

        // Fail fast, before any local VACUUM/read work: an unconfigured sync is guaranteed to
        // fail on push anyway (NullCloudStore.PushAsync throws), so there is no reason to touch
        // the local bank first.
        if (cloud is NullCloudStore)
        {
            throw new SyncNotConfiguredException();
        }

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

            await StripNonSyncableAsync(localSnapshot, cancellationToken).ConfigureAwait(false);

            // 2. Integrity check on the snapshot.
            await EnsureSnapshotIntegrityAsync(localSnapshot, "Local", cancellationToken).ConfigureAwait(false);

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
                (received, reindexed) = await MergeRemoteAsync(cloud, objectKey, projectId, remote.Data, cancellationToken).ConfigureAwait(false);
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

                    await StripNonSyncableAsync(mergedPath, cancellationToken).ConfigureAwait(false);
                    await EnsureSnapshotIntegrityAsync(mergedPath, "Merged", cancellationToken).ConfigureAwait(false);
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
                    // The authenticity tag is embedded directly in the pushed bytes (not a
                    // separate sidecar object): publication is then atomic under the existing
                    // CAS push — there is no window between two writes for a torn publish (a
                    // network blip, or a losing device's concurrent push) to leave a new blob
                    // paired with a stale or missing tag.
                    var passphrase = await ReadPassphraseAsync(cancellationToken).ConfigureAwait(false);
                    var uploadBytes = string.IsNullOrEmpty(passphrase)
                        ? snapshotBytes
                        : _authenticator.Wrap(passphrase, snapshotBytes);

                    var newETag = await cloud.PushAsync(objectKey, uploadBytes, pushETag, cancellationToken)
                        .ConfigureAwait(false);

                    // Record the ETag watermark and, on an encrypted bank, the authenticity
                    // watermark (sync_auth_seen) — same connection, one read of the passphrase
                    // already done above.
                    await using (var conn = await openBank(cancellationToken).ConfigureAwait(false))
                    {
                        await using var upsert = conn.CreateCommand();
                        upsert.CommandText = "INSERT INTO sync_meta (key, value) VALUES ('last_etag', @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                        upsert.Parameters.AddWithValue("@value", newETag);
                        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                        if (string.IsNullOrEmpty(passphrase))
                        {
                            Log.SkippingAuthenticityTagForUnencryptedBank(logger, objectKey);
                        }
                        else
                        {
                            await MarkAuthTagSeenAsync(conn, objectKey, cancellationToken).ConfigureAwait(false);
                        }
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
                        (received, reindexed) = await MergeRemoteAsync(cloud, objectKey, projectId, remote.Data, cancellationToken)
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

                            await StripNonSyncableAsync(retryPath, cancellationToken).ConfigureAwait(false);
                            await EnsureSnapshotIntegrityAsync(retryPath, "Retry-merged", cancellationToken).ConfigureAwait(false);
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

    private async Task<(int Received, int Reindexed)> MergeRemoteAsync(ICloudStore cloud, string objectKey,
        string projectId, byte[] remoteData, CancellationToken cancellationToken)
    {
        var remotePath = Path.GetTempFileName();
        try
        {
            await using var conn = await openBank(cancellationToken).ConfigureAwait(false);

            // The same passphrase ATTACH will key the snapshot with also keys the authenticity
            // check — both read it off the live bank's connection string.
            var attachKey = new SqliteConnectionStringBuilder(conn.ConnectionString).Password;

            // Authenticity check (and, on an encrypted bank, stripping the embedded header)
            // BEFORE integrity (quick_check only detects corruption, not a valid-but-substituted
            // blob) and BEFORE ATTACH — a refused blob must never reach the live bank, and the
            // header/tag must never reach the file SQLite itself opens.
            var snapshotData = await VerifyAndUnwrapRemoteAsync(conn, objectKey, remoteData, attachKey, cancellationToken)
                .ConfigureAwait(false);

            await File.WriteAllBytesAsync(remotePath, snapshotData, cancellationToken).ConfigureAwait(false);

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

            // ATTACH the remote snapshot. When the local bank is encrypted its snapshots are
            // encrypted copies, so the ATTACH needs the same key (quote() produces the literal
            // form Microsoft.Data.Sqlite uses for Password — same trick as the rekey path).
            // A remote in a different encryption state fails here and maps to SyncCorruptFile.
            string attachSql;
            if (attachKey is null)
            {
                attachSql = $"ATTACH DATABASE '{remotePath}' AS remote";
            }
            else
            {
                await using var quote = conn.CreateCommand();
                quote.CommandText = "SELECT quote($key)";
                quote.Parameters.AddWithValue("$key", attachKey);
                attachSql = $"ATTACH DATABASE '{remotePath}' AS remote KEY {(string)(await quote.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!}";
            }

            await using var attach = conn.CreateCommand();
            attach.CommandText = attachSql;
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // The read-write open path refuses a bank stamped newer than CurrentVersion
                // (MemorySchema.EnsureAsync); ATTACH bypasses that guard entirely, so the
                // attached snapshot's own version is checked here before anything is merged.
                await using (var versionCheck = conn.CreateCommand())
                {
                    versionCheck.CommandText = "PRAGMA remote.user_version";
                    var remoteVersion = (long)(await versionCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                    if (remoteVersion > MemorySchema.CurrentVersion)
                    {
                        throw new UnsupportedSchemaVersionException(
                            $"remote snapshot schema v{remoteVersion} is newer than this binary supports (v{MemorySchema.CurrentVersion}); update ai-raccoon");
                    }
                }

                var received = 0;

                // Merge entries: content-addressed near-union (skip duplicates). OR IGNORE also
                // absorbs the unique-bucket constraints: a replica pushing a row the local bank
                // already has is silently skipped and converges on the next write
                // (docs/work/archive/2026-08-06-extraction-followups-plan.md).
                await using (var mergeEntries = conn.CreateCommand())
                {
                    mergeEntries.CommandText = """
                                               INSERT OR IGNORE INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                                               workspace_id, agent_id, created_at, updated_at,
                                                                               access_count, last_accessed_at, rating, ttl_days,
                                                                               embed_state, embedding)
                                               SELECT r.hash, r.path, r.value, r.source_file, r.section, r.scope, r.project_id, r.context_label,
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

                // Resolve source_id for newly merged rows: populate the memory_source table
                // and backfill the FK, mirroring the migration's classification logic.
                await using (var resolveSource = conn.CreateCommand())
                {
                    resolveSource.CommandText = """
                                                INSERT OR IGNORE INTO memory_source (source_type, source_locator, section)
                                                SELECT CASE
                                                        WHEN e.source_file LIKE 'hermes/%' OR e.source_file LIKE '%/hermes/%' THEN 'transcript'
                                                        WHEN e.source_file IS NULL OR e.source_file = '' THEN 'manual'
                                                        ELSE 'file'
                                                    END,
                                                    COALESCE(e.source_file, ''),
                                                    e.section
                                                FROM entries e
                                                WHERE e.source_id IS NULL AND e.workspace_id IS NULL
                                                """;
                    await resolveSource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var backfillSourceId = conn.CreateCommand())
                {
                    backfillSourceId.CommandText = """
                                                   UPDATE entries SET source_id = (
                                                       SELECT ms.id FROM memory_source ms
                                                       WHERE ms.source_locator = COALESCE(entries.source_file, '')
                                                         AND (ms.section IS entries.section OR (ms.section IS NULL AND entries.section IS NULL))
                                                         AND ms.source_type = CASE
                                                             WHEN entries.source_file LIKE 'hermes/%' OR entries.source_file LIKE '%/hermes/%' THEN 'transcript'
                                                             WHEN entries.source_file IS NULL OR entries.source_file = '' THEN 'manual'
                                                             ELSE 'file'
                                                         END
                                                   )
                                                   WHERE source_id IS NULL AND workspace_id IS NULL
                                                   """;
                    await backfillSourceId.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Settings are per-machine (cloud credentials, embedding endpoint/key) and never
                // cross the sync boundary in either direction — push already strips them, and
                // pull must not read remote.settings at all.

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
                    // Nulls structure_embedding/heading_path alongside the content columns: the
                    // vec_structure_pending trigger alone would drop the vec row but leave these stale,
                    // breaking lockstep with entries.structure_embedding and blocking the structure heal pass.
                    reindexCmd.CommandText = """
                                             UPDATE entries
                                             SET embed_state = 'pending', embedding = NULL,
                                                 structure_embedding = NULL, heading_path = NULL
                                             WHERE embed_state = 'embedded'
                                               AND id IN (
                                                   SELECT e.id FROM entries e
                                                   INNER JOIN remote.entries r ON e.hash = r.hash AND COALESCE(e.scope, 'workspace') = COALESCE(r.scope, 'workspace')
                                                   WHERE e.workspace_id IS NULL
                                               )
                                             """;
                    reindexed = await reindexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3): the
                // merge's tombstone DELETE can remove group members and the merge INSERT above
                // can add new source_file-bearing rows to a group; bank-wide is cheap and sync
                // is rare, so there is no reason to scope this to the affected groups.
                // FromIdOrder (GH #371), not the sentinel-guarded form: a merge's pulled rows and a
                // tombstone's survivors both need re-deriving from this bank's own id order — see
                // MemorySql.RecomputeChunkColumnsBankWideFromIdOrder for why that is still safe here.
                await using (var recompute = conn.CreateCommand())
                {
                    recompute.CommandText = MemorySql.RecomputeChunkColumnsBankWideFromIdOrder;
                    await recompute.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Deletes workspace-scoped entries and all settings rows, DROPs the code corpus
    /// (code_entries/code_fts/vec_code — table absence, not row-deletion, so their shadow tables
    /// and triggers drop with them, docs/adr/0014), then VACUUMs the snapshot. Every path that
    /// pushes a snapshot must call this — settings hold the cloud credentials the push itself
    /// authenticates with, and the code corpus never leaves the machine.</summary>
    private async Task StripNonSyncableAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        // The snapshot of an encrypted bank is itself encrypted, so the strip opens through
        // openSnapshot with the bank key, read-write (DELETE + VACUUM) and vec0 loaded (the
        // entry triggers need it).
        await using var snap = await openSnapshot(snapshotPath, cancellationToken).ConfigureAwait(false);
        await using var del = snap.CreateCommand();
        del.CommandText = "DELETE FROM entries WHERE workspace_id IS NOT NULL";
        await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var delSettings = snap.CreateCommand();
        delSettings.CommandText = "DELETE FROM settings";
        await delSettings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // IF EXISTS: a snapshot opened via openSnapshot never ran EnsureAsync, so a bank that
        // predates the code corpus produces a snapshot with none of these tables — a bare DROP
        // would throw SqliteException ("no such table") and abort the push.
        await using var dropCodeEntries = snap.CreateCommand();
        dropCodeEntries.CommandText = "DROP TABLE IF EXISTS code_entries";
        await dropCodeEntries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var dropCodeFts = snap.CreateCommand();
        dropCodeFts.CommandText = "DROP TABLE IF EXISTS code_fts";
        await dropCodeFts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var dropVecCode = snap.CreateCommand();
        dropVecCode.CommandText = "DROP TABLE IF EXISTS vec_code";
        await dropVecCode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The stamped application_id is a digest of the Ddl block that still declares the code
        // corpus (MemorySchema.SchemaDigest) — leaving it in place would make a hand-restored
        // snapshot's EnsureAsync see a matching digest, skip the Ddl block, and never get
        // code_entries/code_fts/vec_code back (integration review S11). Reset it so the next
        // EnsureAsync re-runs the Ddl block unconditionally.
        await using var resetDigest = snap.CreateCommand();
        resetDigest.CommandText = "PRAGMA application_id = 0";
        await resetDigest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var vac = snap.CreateCommand();
        vac.CommandText = "VACUUM";
        await vac.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs PRAGMA quick_check against a snapshot file and throws SyncCorruptFileException when the result is not "ok". Every path that pushes a snapshot must call this.</summary>
    private async Task EnsureSnapshotIntegrityAsync(string snapshotPath, string label, CancellationToken cancellationToken)
    {
        await using var ro = await openReadOnly(snapshotPath, cancellationToken).ConfigureAwait(false);
        await using var integrity = ro.CreateCommand();
        integrity.CommandText = "PRAGMA quick_check";
        var result = (string)(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncCorruptFileException($"{label} snapshot integrity check failed: {result}");
        }
    }

    private async Task WaitForWalCheckpointAsync(CancellationToken cancellationToken)
    {
        await using var conn = await openBank(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadPassphraseAsync(CancellationToken cancellationToken)
    {
        await using var conn = await openBank(cancellationToken).ConfigureAwait(false);
        return new SqliteConnectionStringBuilder(conn.ConnectionString).Password;
    }

    /// <summary>Verifies the pulled remote blob's embedded HMAC tag before quick_check/ATTACH
    /// ever runs on it, returning the snapshot bytes with the header stripped. Unencrypted banks
    /// have no passphrase to key from and skip the check entirely (a keyless checksum would only
    /// re-cover what quick_check already does — integrity, not authenticity). A headerless blob
    /// this objectKey has never carried a verified tag for is a legacy blob predating this
    /// feature and is accepted with a logged warning (first-contact trust: a remote already
    /// tampered with before its first tagged push is indistinguishable from a genuine legacy
    /// blob). A headerless blob for an objectKey that HAS previously carried a verified tag is
    /// refused — the tag can only disappear by an attacker rewriting the whole object, which is
    /// exactly the downgrade a separate sidecar object was vulnerable to at zero extra cost for
    /// an attacker who already has delete access. A present-but-mismatching tag is always
    /// refused.</summary>
    private async Task<byte[]> VerifyAndUnwrapRemoteAsync(SqliteConnection conn, string objectKey, byte[] remoteData,
        string? passphrase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            Log.SkippingAuthenticityCheckForUnencryptedBank(logger, objectKey);
            return remoteData;
        }

        if (_authenticator.TryUnwrap(remoteData, out var tag, out var innerData))
        {
            if (!_authenticator.Verify(passphrase, tag, innerData))
            {
                throw new SyncTamperedRemoteException(
                    $"Remote snapshot '{objectKey}' failed its authenticity check: the embedded HMAC tag does " +
                    "not match its bytes. Refusing to merge — the remote object may have been tampered with, " +
                    "corrupted in transit, or overwritten by a bank using a different passphrase. Verify the " +
                    "remote object, or delete it and let this bank re-push a fresh copy.");
            }

            await MarkAuthTagSeenAsync(conn, objectKey, cancellationToken).ConfigureAwait(false);
            return innerData;
        }

        if (await HasSeenAuthTagAsync(conn, objectKey, cancellationToken).ConfigureAwait(false))
        {
            throw new SyncTamperedRemoteException(
                $"Remote snapshot '{objectKey}' has no authenticity tag, but this bank has previously verified " +
                "one for this object. Refusing rather than trusting a blob that can only lose its tag by being " +
                "rewritten wholesale — a tag-stripping or downgrade attempt, or (harmlessly) a hand-restored " +
                "backup. Push a fresh tagged copy from a trusted bank to recover.");
        }

        Log.RemoteBlobMissingAuthenticityTag(logger, objectKey);
        return remoteData;
    }

    private static string AuthSeenKey(string objectKey) => $"sync_auth_seen:{objectKey}";

    private static async Task MarkAuthTagSeenAsync(SqliteConnection conn, string objectKey, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_meta (key, value) VALUES (@key, '1') ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("@key", AuthSeenKey(objectKey));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasSeenAuthTagAsync(SqliteConnection conn, string objectKey, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sync_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", AuthSeenKey(objectKey));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Warning,
            Message = "Sync If-Match push exhausted after {retries} retries")]
        public static partial void SyncConflictExhausted(ILogger logger, int retries);

        [LoggerMessage(EventId = 101, Level = LogLevel.Debug,
            Message = "Skipping push authenticity tag for '{objectKey}': bank is unencrypted, nothing to key from")]
        public static partial void SkippingAuthenticityTagForUnencryptedBank(ILogger logger, string objectKey);

        [LoggerMessage(EventId = 102, Level = LogLevel.Debug,
            Message = "Skipping authenticity check for '{objectKey}': bank is unencrypted, nothing to key from")]
        public static partial void SkippingAuthenticityCheckForUnencryptedBank(ILogger logger, string objectKey);

        [LoggerMessage(EventId = 103, Level = LogLevel.Warning,
            Message = "Remote snapshot '{objectKey}' has no authenticity tag (legacy blob predating HMAC verification) — accepting with a warning")]
        public static partial void RemoteBlobMissingAuthenticityTag(ILogger logger, string objectKey);
    }
}
