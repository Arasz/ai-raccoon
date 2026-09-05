using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
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
    ISyncBlobAuthenticator? blobAuthenticator = null,
    ProjectIdAliasMap? aliasMap = null) : ISyncService
{
    private const int MaxPushRetries = 3;
    private readonly ISyncBlobAuthenticator _authenticator = blobAuthenticator ?? new SyncBlobAuthenticator();
    private readonly ProjectIdAliasMap? _aliasMap = aliasMap;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Package E: an explicitly injected map wins (tests); otherwise the live choke-point
    /// cache — read per merge, never frozen at construction, so a reloaded Default applies to the
    /// next sync without a restart.</summary>
    private ProjectIdAliasMap ResolveAliasMap() => _aliasMap ?? ProjectIdAliasMap.Default;

    /// <summary>Tables that never leave the machine via sync — DROPped from every pushed snapshot
    /// by <see cref="StripNonSyncableAsync" />. The single list both that method and its tests read,
    /// so a table added here only needs adding once.</summary>
    internal static readonly string[] MachineLocalTables =
    [
        "workspaces", "promotion_queue", "promotion_queue_prune_requests", "repair_requests"
    ];

    /// <summary>
    ///     Pull-time id fold: resolves the remote id through the REMOTE projects-row name first — a
    ///     guid loser registered under its pre-guid name is attributed through that name, never a
    ///     hardcoded guid — then through the caller-supplied alias CASE, falling back to the raw id
    ///     when neither resolves (an unknown guid, or an id whose projects row is missing, passes
    ///     through untouched). Built from the one-shot repair map (same content the CLI dry-run
    ///     planner and the repair job consume), inlined as a CASE because the merge is SQL — the
    ///     entries are operator-supplied per repair, never bank content. The remote.projects
    ///     table is always ATTACHed (the merge attaches the whole snapshot, and projects never strips).
    ///     <para>
    ///         d-426 SHOULD-3: canonical self-arms (<c>WHEN 'new-id' THEN 'new-id'</c>) catch a remote
    ///         guid row whose projects-row name resolves to a canonical winner rather than an alias
    ///         — without one the CASE falls to ELSE and the raw guid leaks back as canonical.
    ///         Alias arms stay ahead of the self-arms: the sets are disjoint today, but an overlap
    ///         must resolve to the alias winner. Every interpolated literal passes through
    ///         <see cref="EscapeSqlString" /> so a future quote in the table cannot break the merge.
    ///     </para>
    ///     <para>
    ///         ADR-0099: an empty map degrades to the name-resolution alone (no CASE wrapper — SQLite
    ///         rejects a CASE with zero WHEN arms). Steady state with the empty default is
    ///         pass-through by definition.
    ///     </para>
    /// </summary>
    internal static string FoldRemoteProjectId(string column) => FoldRemoteProjectId(column, ProjectIdAliasMap.Default);

    /// <summary>Folds through an explicit one-shot repair map instead of the steady-state default.</summary>
    internal static string FoldRemoteProjectId(string column, ProjectIdAliasMap map)
    {
        var named =
            $"COALESCE((SELECT p.name FROM remote.projects p WHERE p.id = {column}), {column})";
        if (map.IsEmpty)
        {
            return named;
        }

        var arms = map.Aliases
            .Select(entry => $"WHEN '{EscapeSqlString(entry.Alias)}' THEN '{EscapeSqlString(entry.Canonical)}'");
        var selfArms = map.Canonicals
            .Select(canonical => $"WHEN '{EscapeSqlString(canonical)}' THEN '{EscapeSqlString(canonical)}'");
        return $"CASE {named} {string.Join(" ", arms)} {string.Join(" ", selfArms)} ELSE {column} END";
    }

    /// <summary>SQL string-literal escape for fold arms: the table is compile-time constants today, but an unescaped quote would break the merge or worse.</summary>
    internal static string EscapeSqlString(string value) => value.Replace("'", "''");

    /// <summary>
    ///     Local-bank fold CASE over an explicit map (Package E2 push symmetry): alias arms only —
    ///     canonical self-arms would be identity rewrites. The caller skips the UPDATE entirely when
    ///     the map is empty, so a mapless snapshot carries exactly what the bank holds.
    /// </summary>
    internal static string FoldLocalProjectId(string column, ProjectIdAliasMap map)
    {
        var arms = map.Aliases
            .Select(entry => $"WHEN '{EscapeSqlString(entry.Alias)}' THEN '{EscapeSqlString(entry.Canonical)}'");
        return $"CASE {column} {string.Join(" ", arms)} ELSE {column} END";
    }

    private static async Task<bool> AliasTableExistsAsync(SqliteConnection conn, string schema,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = 'project_id_aliases'";
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    /// <summary>
    ///     The E2 conflict probe: a locally mapped alias the remote maps to a different winner
    ///     (or a different kind) aborts the merge before entries move — first-writer-wins keeps the
    ///     local row, and the throw names both winners for the human who must pick the canonical one.
    /// </summary>
    private static async Task ThrowOnAliasConflictAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT l.alias, l.winner, r.winner
            FROM project_id_aliases l
            JOIN remote.project_id_aliases r ON r.alias = l.alias
            WHERE COALESCE(l.winner, '') <> COALESCE(r.winner, '') OR l.kind <> r.kind
            LIMIT 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SyncAliasConflictException(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    /// <summary>Convenience ctor for a fixed store (tests); the DI path resolves per call.</summary>
    public SyncService(ICloudStore cloud, Func<CancellationToken, Task<SqliteConnection>> openBank,
        Func<string, CancellationToken, Task<SqliteConnection>> openSnapshot,
        Func<string, CancellationToken, Task<SqliteConnection>> openReadOnly,
        TimeProvider timeProvider, ILogger<SyncService> logger, ProjectIdAliasMap? aliasMap = null)
        : this(_ => Task.FromResult(cloud), openBank, openSnapshot, openReadOnly, timeProvider, logger, null, aliasMap)
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

                // Package E2 pull arm for project_id_aliases (row-merge per H2 — the table does
                // NOT ride free): insert-only, alias-PK first-writer-wins; a
                // same-alias-different-winner row is a genuine conflict and aborts the merge
                // before anything else mutates, surfacing for a human. Either side may predate
                // v14 (no table) — then there is nothing to merge and the arm skips. A merged map
                // reloads the choke-point cache off the table, so the next write folds through it.
                if (await AliasTableExistsAsync(conn, "main", cancellationToken).ConfigureAwait(false) &&
                    await AliasTableExistsAsync(conn, "remote", cancellationToken).ConfigureAwait(false))
                {
                    await ThrowOnAliasConflictAsync(conn, cancellationToken).ConfigureAwait(false);
                    await using (var mergeAliases = conn.CreateCommand())
                    {
                        mergeAliases.CommandText = """
                            INSERT OR IGNORE INTO project_id_aliases (alias, winner, kind, applied_at)
                            SELECT alias, winner, kind, applied_at FROM remote.project_id_aliases
                            """;
                        await mergeAliases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await ProjectIdAliases.LoadAndCacheAsync(conn, logger, cancellationToken).ConfigureAwait(false);
                }

                // Merge entries: content-addressed near-union (skip duplicates). OR IGNORE also
                // absorbs the unique-bucket constraints: a replica pushing a row the local bank
                // already has is silently skipped and converges on the next write
                // (docs/work/archive/2026-08-06-extraction-followups-plan.md).
                // Air-merge P2: the stored project_id and the tombstone check both fold through
                // FoldRemoteProjectId, so an unrepaired replica's loser rows land canonical and its
                // loser pushes stay suppressed by the repair's rewritten (winner-keyed) tombstones.
                await using (var mergeEntries = conn.CreateCommand())
                {
                    var foldedRemoteProject = FoldRemoteProjectId("r.project_id", ResolveAliasMap());
                    // d-426 SHOULD-4: the pull fold matches the repair's domain (project-scope rows
                    // only) — custom/shared rows merge verbatim, exactly as the repair leaves them.
                    // The tombstone-suppression check below stays folded: identity compares in
                    // fold-space while storage stays verbatim, so a winner-keyed tombstone still
                    // suppresses the content it describes.
                    var scopedFold =
                        $"CASE WHEN {ProjectRows.ScopeIsProject("r.")} THEN {foldedRemoteProject} ELSE r.project_id END";
                    mergeEntries.CommandText = $"""
                                               INSERT OR IGNORE INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                                               workspace_id, agent_id, created_at, updated_at,
                                                                               access_count, last_accessed_at, rating, ttl_days,
                                                                               embed_state, embedding)
                                               SELECT r.hash, r.path, r.value, r.source_file, r.section, r.scope, {scopedFold}, r.context_label,
                                                      r.workspace_id, r.agent_id, r.created_at, r.updated_at,
                                                      r.access_count, r.last_accessed_at, r.rating, r.ttl_days,
                                                      'pending', NULL
                                               FROM remote.entries r
                                               WHERE r.workspace_id IS NULL
                                                 AND NOT EXISTS (
                                                     SELECT 1 FROM sync_tombstones t
                                                     WHERE t.hash = r.hash
                                                       AND t.scope = COALESCE(r.scope, 'workspace')
                                                       AND t.project_id = {foldedRemoteProject}
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

                // Merge sync_tombstones: union, folding loser ids so an unrepaired replica's
                // loser tombstone meets the repair's rewritten winner-keyed one (OR IGNORE dedups).
                await using (var mergeTombstones = conn.CreateCommand())
                {
                    var foldedTombstoneProject = FoldRemoteProjectId("project_id", ResolveAliasMap());
                    mergeTombstones.CommandText = $"""
                                                  INSERT OR IGNORE INTO sync_tombstones (project_id, hash, scope, deleted_at)
                                                  SELECT {foldedTombstoneProject}, hash, scope, deleted_at FROM remote.sync_tombstones
                                                  """;
                    await mergeTombstones.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                // Apply tombstones: delete rows locally that remote deleted — the remote side folds
                // too, so a loser tombstone from an unrepaired replica still deletes the locally
                // folded winner row.
                await using (var applyTombstones = conn.CreateCommand())
                {
                    var foldedApplyProject = FoldRemoteProjectId("project_id", ResolveAliasMap());
                    applyTombstones.CommandText = $"""
                                                  DELETE FROM entries
                                                  WHERE (hash, COALESCE(scope, 'workspace'), project_id)
                                                      IN (SELECT hash, scope, {foldedApplyProject} FROM remote.sync_tombstones)
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

    /// <summary>Deletes workspace-scoped entries and all settings rows, folds loser-keyed ids to
    /// their winners in the pushed snapshot (Package E2 push symmetry — see
    /// <see cref="FoldSnapshotProjectIdsAsync" />), DROPs the code corpus
    /// (code_entries/code_fts/vec_code — table absence, not row-deletion, so their shadow tables
    /// and triggers drop with them, docs/adr/0014) and DROPs the telemetry tables
    /// (search_quality/metrics — table absence sheds their indexes too and lets a restored
    /// snapshot's EnsureAsync recreate them via the digest-DDL path, docs/adr/0098), then VACUUMs
    /// the snapshot. Every path that pushes a snapshot must call this — settings hold the cloud
    /// credentials the push itself authenticates with, and the code corpus and telemetry never
    /// leave the machine.</summary>
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

        await FoldSnapshotProjectIdsAsync(snap, cancellationToken).ConfigureAwait(false);

        foreach (var table in MachineLocalTables)
        {
            await using var dropTable = snap.CreateCommand();
            dropTable.CommandText = $"DROP TABLE IF EXISTS {table}";
            await dropTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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

        // ADR-0098: telemetry never syncs — the merge reads entries + sync_tombstones only, so
        // nothing consumes synced telemetry. DROP (not DELETE): telemetry has no FTS/shadow
        // tables or triggers, so DROP buys table-absence + index shed + restore-via-digest-DDL.
        // IF EXISTS: a pre-telemetry snapshot has neither table — a bare DROP would throw and
        // abort the push, same H6 shape as the code corpus above.
        await using var dropSearchQuality = snap.CreateCommand();
        dropSearchQuality.CommandText = "DROP TABLE IF EXISTS search_quality";
        await dropSearchQuality.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var dropMetrics = snap.CreateCommand();
        dropMetrics.CommandText = "DROP TABLE IF EXISTS metrics";
        await dropMetrics.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    ///     Package E2 push symmetry: an unrepaired replica's loser-keyed entries and tombstones
    ///     land canonical in the uploaded snapshot — the same fold the pull applies on receipt, so
    ///     both directions converge without a repair on the pushing side. Empty map: no CASE, no
    ///     UPDATE, the snapshot carries exactly what the bank holds. Dropped-keyed rows stay verbatim
    ///     here; the receiving pull's tombstone suppression removes what the repair deleted.
    /// </summary>
    private async Task FoldSnapshotProjectIdsAsync(SqliteConnection snap, CancellationToken cancellationToken)
    {
        var map = ResolveAliasMap();
        if (map.IsEmpty)
        {
            return;
        }

        var fold = FoldLocalProjectId("project_id", map);
        var losers = string.Join(", ", map.Aliases.Select(entry => $"'{EscapeSqlString(entry.Alias)}'"));
        foreach (var table in new[] { "entries", "sync_tombstones" })
        {
            await using var exists = snap.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
            exists.Parameters.AddWithValue("@table", table);
            if ((long)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
            {
                continue;
            }

            await using var foldIds = snap.CreateCommand();
            foldIds.CommandText = $"UPDATE {table} SET project_id = {fold} WHERE project_id IN ({losers})";
            await foldIds.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
