using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL over our memory.db tables (see docs/work/2026-08-03-native-memory-plan.md §2.2); kept in one place so the store stays thin.</summary>
internal static class MemorySql
{
    // The vec0 partition key (docs/plans/2026-08-08-search-knn-perf.md §3.1): one expression maps
    // an entries row to its search context, used identically by the vec0 triggers, the v2 migration
    // backfill and the chunk-column recompute. Length-prefixed (not ':'-joined) because a bare
    // 'custom:' || project_id || ':' || label collides: (project_id='a:b', label='c') and
    // (project_id='a', label='b:c') both produce 'custom:a:b:c'.
    public static string ContextKeyExpression(string prefix) => $"""
                                                                  CASE
                                                                      WHEN {prefix}workspace_id IS NOT NULL
                                                                           THEN 'workspace:' || length({prefix}project_id) || ':' || {prefix}project_id || ':' || {prefix}workspace_id
                                                                      WHEN {prefix}scope = 'shared'  THEN 'shared'
                                                                      WHEN {prefix}scope = 'project' THEN 'project:' || {prefix}project_id
                                                                      ELSE 'custom:' || length({prefix}project_id) || ':' || {prefix}project_id || ':' || COALESCE({prefix}context_label, '')
                                                                  END
                                                                  """;

    /// <summary>
    ///     The C#-side twin of <see cref="ContextKeyExpression" />, parsing a search context string
    ///     (see SearchContexts/FilterFor) into the same key. Mirrors FilterFor's branches exactly,
    ///     including its quirks (the project branch reads the project id out of the context string,
    ///     not the <paramref name="projectId" /> argument) so the two never diverge on a real query.
    /// </summary>
    public static string ContextKeyFor(string context, string projectId)
    {
        if (context == ContextNaming.SharedContext)
        {
            return "shared";
        }

        if (context.StartsWith("project:", StringComparison.Ordinal))
        {
            return $"project:{context["project:".Length..]}";
        }

        if (context.StartsWith("workspace:", StringComparison.Ordinal))
        {
            return $"workspace:{projectId.Length}:{projectId}:{context["workspace:".Length..]}";
        }

        if (context.StartsWith("label:", StringComparison.Ordinal))
        {
            var rest = context["label:".Length..];
            var colon = rest.IndexOf(':');
            if (colon > 0)
            {
                return $"custom:{projectId.Length}:{projectId}:{rest[(colon + 1)..]}";
            }
        }

        return $"custom:{projectId.Length}:{projectId}:{context}";
    }

    // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3): recompute
    // chunk_index/total_chunks for one (ctx, source_file) group after a write that can change
    // group membership. Numbered per ctx rather than per raw scope/project/label/workspace
    // columns — the project-scope asymmetry (a labelled project row ignores its label) only
    // holds when both the trigger and this recompute key off the same ctx expression.
    public static readonly string RecomputeChunkColumnsForContext = $"""
                                                                      WITH numbered AS (
                                                                          SELECT id,
                                                                                 ROW_NUMBER() OVER (PARTITION BY {ContextKeyExpression("")}, source_file ORDER BY id) - 1 AS ci,
                                                                                 COUNT(*)     OVER (PARTITION BY {ContextKeyExpression("")}, source_file)              AS tc
                                                                          FROM entries
                                                                          WHERE source_file IS NOT NULL AND ({ContextKeyExpression("")}) = @ctx AND source_file = @sourceFile)
                                                                      UPDATE entries
                                                                         SET chunk_index  = (SELECT ci FROM numbered n WHERE n.id = entries.id),
                                                                             total_chunks = (SELECT tc FROM numbered n WHERE n.id = entries.id)
                                                                       WHERE entries.id IN (SELECT id FROM numbered)
                                                                      """;

    /// <summary>Bank-wide form of <see cref="RecomputeChunkColumnsForContext" /> — no ctx/source_file predicate, used by the v2 migration backfill and after a sync merge.</summary>
    public static readonly string RecomputeChunkColumnsBankWide = $"""
                                                                    WITH numbered AS (
                                                                        SELECT id,
                                                                               ROW_NUMBER() OVER (PARTITION BY {ContextKeyExpression("")}, source_file ORDER BY id) - 1 AS ci,
                                                                               COUNT(*)     OVER (PARTITION BY {ContextKeyExpression("")}, source_file)              AS tc
                                                                        FROM entries
                                                                        WHERE source_file IS NOT NULL)
                                                                    UPDATE entries
                                                                       SET chunk_index  = (SELECT ci FROM numbered n WHERE n.id = entries.id),
                                                                           total_chunks = (SELECT tc FROM numbered n WHERE n.id = entries.id)
                                                                     WHERE entries.id IN (SELECT id FROM numbered)
                                                                    """;

    // embed_state defaults to 'pending': every write lands deferred until the embed pipeline runs.
    // ON CONFLICT DO NOTHING (bare — expression/partial unique indexes cannot be conflict targets)
    // makes concurrent same-bucket inserts converge: the loser returns the winner's row via the
    // post-insert bucket-key re-read (F3; see docs/work/2026-08-06-extraction-followups-plan.md).
    public const string InsertEntry = """
                                      INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                           workspace_id, agent_id, created_at, updated_at)
                                      VALUES (@hash, @path, @value, @sourceFile, @section, @scope, @projectId, @contextLabel,
                                              @workspaceId, @agentId, @createdAt, @updatedAt)
                                      ON CONFLICT DO NOTHING
                                      """;

    public const string SelectEntryById = """
                                          SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                 project_id AS ProjectId, context_label AS ContextLabel,
                                                 workspace_id AS WorkspaceId, created_at AS CreatedAt
                                          FROM entries
                                          WHERE id = @id
                                          """;

    public const string SelectSourceByHashAndProject = """"
                                                        SELECT path AS Path, value AS Value, source_file AS SourceFile, section AS Section
                                                       FROM entries
                                                       WHERE hash = @hash AND scope = 'project' AND project_id = @projectId
                                                       LIMIT 1
                                                       """";

    public const string SelectExtractionCandidates = """"
                                                      SELECT hash AS Hash, path AS Path, value AS Value, source_file AS SourceFile,
                                                             rating AS Rating, access_count AS AccessCount, created_at AS CreatedAt,
                                                             ttl_days AS TtlDays
                                                      FROM entries
                                                      WHERE scope = 'project' AND project_id = @projectId AND embed_state = 'embedded'
                                                        AND (@includeTtlRows = 1 OR ttl_days IS NULL)
                                                      """";

    public const string SelectSharedIndex = """"
                                             SELECT path AS Path, value AS Value
                                             FROM entries
                                             WHERE scope = 'shared'
                                             """";

    public const string SelectProjectIds = """"
                                             SELECT DISTINCT project_id AS ProjectId
                                             FROM entries
                                             WHERE scope = 'project'
                                             ORDER BY project_id
                                             """";

    // Global content dedup (FR-NM-7; see docs/work/features-native-memory/native-memory.feature): the earliest committed row (workspace_id IS NULL) holding
    // this value, across every scope of the project — writing identical content returns it.
    public const string SelectCommittedByValue = """
                                                 SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                        project_id AS ProjectId, context_label AS ContextLabel,
                                                        workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                 FROM entries
                                                 WHERE value = @value AND workspace_id IS NULL AND project_id = @projectId
                                                 ORDER BY id
                                                 LIMIT 1
                                                 """;

    public const string EntryExistsByPathInBucket = """
                                                    SELECT 1 FROM entries
                                                    WHERE path = @path AND scope IS @scope AND project_id = @projectId
                                                      AND context_label IS @contextLabel AND workspace_id IS @workspaceId
                                                    LIMIT 1
                                                    """;

    // The shared tier is cross-project (uq_entries_shared_bucket is global): the loser of a
    // concurrent cross-project promote must find the winner's row without a project filter (F3).
    public const string SelectSharedEntryByPathAndHash = """
                                                         SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                                project_id AS ProjectId, context_label AS ContextLabel,
                                                                workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                         FROM entries
                                                         WHERE path = @path AND hash = @hash AND scope = 'shared'
                                                           AND workspace_id IS NULL
                                                         LIMIT 1
                                                         """;

    public const string SelectChunkIdByPathAndHashInBucket = """
                                                             SELECT id FROM entries
                                                             WHERE path = @path AND hash = @hash
                                                               AND scope IS @scope AND project_id = @projectId
                                                               AND context_label IS @contextLabel AND workspace_id IS @workspaceId
                                                             LIMIT 1
                                                             """;

    public const string EntryExistsByPathAndHashInBucket = """
                                                           SELECT 1 FROM entries
                                                           WHERE path = @path AND hash = @hash
                                                             AND scope IS @scope AND project_id = @projectId
                                                             AND context_label IS @contextLabel AND workspace_id IS @workspaceId
                                                           LIMIT 1
                                                           """;

    // (see docs/plans/retrieval-improvement-c.md §3 2c): the FTS index carries source_file and section as weighted
    // columns — bm25(entries_fts, 1.0, 8.0, 16.0) gives a source-path match eight times and
    // a section match sixteen times the signal of a body-text match, so identifier tokens
    // (ADR-0070) and section tokens (decision) rank the owning chunk above cross-referencing
    // prose. ChunkIndex/TotalChunks are persisted columns (docs/plans/2026-08-08-search-knn-perf.md
    // §3.2/§3.3) rather than a per-query window function — the old MATERIALIZED CTE existed only
    // to keep FTS5's bm25() from sharing a SELECT with a window function.
    public const string SearchByFilter = """
                                         SELECT e.hash AS Hash, 0 AS Seq, bm25(entries_fts, 1.0, 8.0, 16.0) AS Ranking,
                                                e.path AS Path, snippet(entries_fts, 0, '', '', '…', 12) AS Snippet,
                                                e.value AS Value, e.source_file AS SourceFile,
                                                e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks
                                         FROM entries_fts
                                         JOIN entries e ON e.id = entries_fts.rowid
                                         WHERE entries_fts MATCH @query AND {filter}
                                         ORDER BY bm25(entries_fts, 1.0, 8.0, 16.0)
                                         LIMIT @limit
                                         """;

    // vec0 modality: native KNN over the ctx partition (docs/plans/2026-08-08-search-knn-perf.md
    // §3.4) — replaces `WHERE {filter}` entirely, since the partition already selects exactly the
    // rows a search context can retrieve (§3.1). Ordered by distance ascending so the row position
    // is the rank for RRF; `ORDER BY v.distance, e.path` on top of the KNN keeps the existing
    // distance-tie break (verified: vec0 preserves it). Vector hits carry a fallback snippet built
    // in C# from the entry value (the FTS list's snippet() payload wins for docs both modalities
    // retrieve). The content list feeds the dual-vector fusion.
    public const string VectorSearchByFilter = """
                                               SELECT e.hash AS Hash, 0 AS Seq, e.path AS Path, e.value AS Value,
                                                      v.distance AS Distance, e.source_file AS SourceFile,
                                                      e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks
                                               FROM vec_entries v
                                               JOIN entries e ON e.id = v.rowid
                                               WHERE v.ctx = @ctx AND v.embedding MATCH @queryVector AND k = @limit
                                               ORDER BY v.distance, e.path
                                               """;

    // Structure modality: cosine KNN over the heading-path vectors (vec_structure),
    // same shape as the content query (source identity included) so both lists fuse in C# by
    // entry hash.
    public const string StructureVectorSearchByFilter = """
                                                        SELECT e.hash AS Hash, 0 AS Seq, e.path AS Path, e.value AS Value,
                                                               v.distance AS Distance, e.source_file AS SourceFile,
                                                               e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks
                                                        FROM vec_structure v
                                                        JOIN entries e ON e.id = v.rowid
                                                        WHERE v.ctx = @ctx AND v.embedding MATCH @queryVector AND k = @limit
                                                        ORDER BY v.distance, e.path
                                                        """;

    public const string DeleteByHashAndProject =
        "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId";

    // Sync propagates deletes through tombstones (FR-NM-8): the row's committed scope is
    // recorded before the delete so sync can suppress resurrection and ship the tombstone.
    public const string SelectScopeByHashAndProject =
        "SELECT scope FROM entries WHERE hash = @hash AND project_id = @projectId";

    public const string UpsertTombstone =
        "INSERT INTO sync_tombstones (hash, scope, deleted_at) VALUES (@hash, @scope, @deletedAt) " +
        "ON CONFLICT(hash, scope) DO UPDATE SET deleted_at = excluded.deleted_at";

    // Mirror delete/rename: committed chunks of the source path AND everything under it
    // (a deleted directory cascades to its subtree; workspace scratch is transient and
    // stays), plus the per-path watch fingerprints so a delete-then-recreate cycle cannot
    // hash-skip its way back to stale chunks. The watch registration survives.
    public const string DeleteBySourcePath = """
                                             DELETE FROM entries
                                             WHERE project_id = @projectId AND workspace_id IS NULL
                                               AND (source_file = @path OR source_file LIKE @pathPrefix ESCAPE '\')
                                             """;

    public const string DeleteWatchFilesByProjectPathCascade = """
                                                               DELETE FROM watch_files
                                                               WHERE project_id = @projectId
                                                                 AND (path = @path OR path LIKE @pathPrefix ESCAPE '\')
                                                               """;

    public const string InsertWatchIfAbsent = """
                                              INSERT INTO watches (project_id, path, created_at, last_change_ts)
                                              VALUES (@projectId, @path, @createdAt, @lastChangeTs)
                                              ON CONFLICT(project_id, path) DO NOTHING
                                              """;

    public const string UpdateWatchLastChange = """
                                                UPDATE watches
                                                SET last_change_ts = @lastChangeTs
                                                WHERE project_id = @projectId AND path = @path
                                                """;

    public const string DeleteWatch =
        "DELETE FROM watches WHERE project_id = @projectId AND path = @path";

    public const string SelectWatches = """
                                        SELECT project_id AS ProjectId, path AS Path, created_at AS CreatedAt,
                                               last_change_ts AS LastChangeTs
                                        FROM watches
                                        ORDER BY project_id, path
                                        """;

    // WP4 cross-process scan lease (D-2; docs/plans/2026-08-07-watch-scan-runaway-fix.md): the
    // lease lives on the watches row, not a separate table, so a row delete is lease release.
    // ExecuteAsync(...) == 1 tells the caller whether the grant/renewal succeeded.

    // Grants when unowned, re-entrant for the current owner, or the previous owner's lease expired.
    public const string AcquireWatchScanLease = """
                                                UPDATE watches SET scan_owner = @owner, scan_lease_expires_at = @expiresAt
                                                WHERE project_id = @projectId AND path = @path
                                                  AND (scan_owner IS NULL OR scan_owner = @owner OR scan_lease_expires_at <= @now)
                                                """;

    // Only the owner may renew, and only while the row still exists — a watch removed out from
    // under a running scan makes this return zero rows at the next renewal, with no extra check.
    public const string RenewWatchScanLease = """
                                              UPDATE watches SET scan_lease_expires_at = @expiresAt
                                              WHERE project_id = @projectId AND path = @path AND scan_owner = @owner
                                              """;

    public const string ReleaseWatchScanLease = """
                                                UPDATE watches SET scan_owner = NULL, scan_lease_expires_at = 0
                                                WHERE project_id = @projectId AND path = @path AND scan_owner = @owner
                                                """;

    public const string UpsertWatchFile = """
                                          INSERT INTO watch_files (project_id, path, file_hash, updated_at)
                                          VALUES (@projectId, @path, @fileHash, @updatedAt)
                                          ON CONFLICT(project_id, path) DO UPDATE SET
                                              file_hash = excluded.file_hash,
                                              updated_at = excluded.updated_at
                                          """;

    public const string SelectWatchFile = """
                                          SELECT file_hash AS FileHash, updated_at AS UpdatedAt
                                          FROM watch_files
                                          WHERE project_id = @projectId AND path = @path
                                          LIMIT 1
                                          """;

    public const string SelectWatchFilesByProject =
        "SELECT path FROM watch_files WHERE project_id = @projectId";

    public const string CountProjectEntries =
        "SELECT count(*) FROM entries WHERE scope = 'project' AND project_id = @projectId";

    public const string PendingCount =
        "SELECT count(*) FROM entries WHERE embed_state = 'pending' AND project_id = @projectId";

    public const string SelectPendingForEmbed =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'pending' AND project_id = @projectId " +
        "ORDER BY id LIMIT @limit";

    public const string MarkEmbedded =
        "UPDATE entries SET embed_state = 'embedded', embedding = @embedding WHERE id = @id";

    public const string SelectEmbeddedForProject =
        "SELECT id AS Id, value AS Value FROM entries WHERE project_id = @projectId AND embed_state = 'embedded' " +
        "ORDER BY id";

    public const string SelectAllEmbedded =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'embedded' ORDER BY id";

    public const string CommittedContexts = """
                                            SELECT DISTINCT CASE WHEN scope = 'shared' THEN 'shared' ELSE 'project:' || project_id END AS context
                                            FROM entries
                                            WHERE scope IN ('shared', 'project')
                                            ORDER BY CASE WHEN scope = 'shared' THEN 0 ELSE 1 END, context
                                            """;

    public const string DistinctFilePaths = """
                                            SELECT DISTINCT path
                                            FROM entries
                                            WHERE scope IN ('shared', 'project')
                                              AND (project_id = @projectId OR scope = 'shared')
                                              AND path IS NOT NULL
                                            ORDER BY path
                                            """;

    public const string SelectRatingForBump =
        "SELECT created_at AS CreatedAt, access_count AS AccessCount FROM entries WHERE hash = @hash LIMIT 1";

    public const string BumpAccess =
        """
        UPDATE entries
        SET access_count = access_count + 1,
            last_accessed_at = @now,
            rating = @rating
        WHERE hash = @hash
        """;

    public const string UpsertSetting = """"
                                        INSERT INTO settings (key, value) VALUES (@key, @value)
                                        ON CONFLICT(key) DO UPDATE SET value = excluded.value
                                        """";

    public const string SelectSetting =
        """
        SELECT value FROM settings WHERE key = @key LIMIT 1
        """;

    public const string SelectWorkspaceStatus =
        """
        SELECT status FROM workspaces WHERE id = @workspaceId AND project_id = @projectId LIMIT 1
        """;

    public const string SelectSettingsByPrefix =
        """
        SELECT key, value FROM settings WHERE key LIKE @prefix || '%' ORDER BY key
        """;

    public const string DeleteSetting =
        """
        DELETE FROM settings WHERE key = @key
        """;

    public const string UpdateEntryTtl =
        """
        UPDATE entries
        SET ttl_days = @ttlDays
        WHERE project_id = @projectId AND hash = @hash
        """;

    public const string SelectEntryMetadata =
        """
        SELECT rating AS Rating, ttl_days AS TtlDays
        FROM entries
        WHERE project_id = @projectId AND hash = @hash
        LIMIT 1
        """;

    public const string SelectEntriesByContext = """
                                                 SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                        project_id AS ProjectId, context_label AS ContextLabel,
                                                        workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                 FROM entries
                                                 WHERE {filter}
                                                 ORDER BY created_at DESC, id DESC
                                                 """;

    public const string SelectEntryByPathInBucket = """
                                                    SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                           project_id AS ProjectId, context_label AS ContextLabel,
                                                           workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                    FROM entries
                                                    WHERE path = @path AND scope IS @scope AND project_id = @projectId
                                                      AND context_label IS @contextLabel AND workspace_id IS @workspaceId
                                                    LIMIT 1
                                                    """;
}
