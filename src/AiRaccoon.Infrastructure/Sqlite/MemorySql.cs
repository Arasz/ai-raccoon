using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL over our memory.db tables (see docs/work/archive/2026-08-03-native-memory-plan.md §2.2); kept in one place so the store stays thin.</summary>
internal static class MemorySql
{
    // The vec0 partition key (docs/plans/2026-08-08-search-knn-perf.md §3.1). Length-prefixed, not
    // ':'-joined, because the naive join collides across project id/label boundaries.
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
    ///     into the same key. Mirrors FilterFor's branches, including reading the project id from the
    ///     context string rather than <paramref name="projectId" />, so the two never diverge.
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

    // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3): recomputes
    // chunk_index/total_chunks for one (ctx, source_file) group after a membership-changing write.
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

    // ON CONFLICT DO NOTHING is bare — expression/partial indexes can't be a conflict target — so
    // concurrent same-bucket inserts converge; the loser re-reads by bucket key
    // (docs/work/archive/2026-08-06-extraction-followups-plan.md).
    public const string InsertEntry = """
                                      INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                           workspace_id, agent_id, created_at, updated_at, source_id)
                                      VALUES (@hash, @path, @value, @sourceFile, @section, @scope, @projectId, @contextLabel,
                                              @workspaceId, @agentId, @createdAt, @updatedAt, @sourceId)
                                      ON CONFLICT DO NOTHING
                                      """;

    public const string SelectEntryById = """
                                          SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                 project_id AS ProjectId, context_label AS ContextLabel,
                                                 workspace_id AS WorkspaceId, created_at AS CreatedAt
                                          FROM entries
                                          WHERE id = @id
                                          """;

    public const string SelectSourceByHashAndProject = """"""
                                                        SELECT e.path AS Path, e.value AS Value, e.source_file AS SourceFile, e.section AS Section,
                                                               ms.source_type AS SourceType, ms.heading_path AS HeadingPath
                                                       FROM entries e
                                                       LEFT JOIN memory_source ms ON ms.id = e.source_id
                                                       WHERE e.hash = @hash AND e.scope = 'project' AND e.project_id = @projectId
                                                       LIMIT 1
                                                       """""";

    public const string SelectExtractionCandidates = """"""
                                                      SELECT e.hash AS Hash, e.path AS Path, e.value AS Value, e.source_file AS SourceFile,
                                                             ms.source_type AS SourceType,
                                                             e.rating AS Rating, e.access_count AS AccessCount, e.created_at AS CreatedAt,
                                                             e.ttl_days AS TtlDays
                                                      FROM entries e
                                                      LEFT JOIN memory_source ms ON ms.id = e.source_id
                                                      WHERE e.scope = 'project' AND e.project_id = @projectId AND e.embed_state = 'embedded'
                                                        AND (@includeTtlRows = 1 OR e.ttl_days IS NULL)
                                                      """""";

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
    // concurrent cross-project promote must find the winner's row without a project filter.
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

    // bm25(entries_fts, 1.0, 8.0, 16.0) weights source_file/section matches 8x/16x a body-text
    // match, so identifier and section tokens outrank cross-referencing prose
    // (docs/plans/retrieval-improvement-c.md §3 2c). ChunkIndex/TotalChunks are persisted columns,
    // not a per-query window function (docs/plans/2026-08-08-search-knn-perf.md §3.2/§3.3).
    // snippet() is deferred to ranking survivors (docs/plans/2026-08-08-search-knn-perf.md §WP7) —
    // FtsSnippetsForSurvivors resolves it there instead.
    public const string SearchByFilter = """
                                         SELECT e.hash AS Hash, 0 AS Seq, bm25(entries_fts, 1.0, 8.0, 16.0) AS Ranking,
                                                e.path AS Path, e.value AS Value, e.source_file AS SourceFile,
                                                e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks,
                                                e.id AS Id
                                         FROM entries_fts
                                         JOIN entries e ON e.id = entries_fts.rowid
                                         WHERE entries_fts MATCH @query AND {filter}
                                         ORDER BY bm25(entries_fts, 1.0, 8.0, 16.0)
                                         LIMIT @limit
                                         """;

    // Re-runs the SAME @query text SearchByFilter used, restricted to the ranking survivors' rowids,
    // so the highlighting matches what the eager statement would have produced. Filters by
    // entries_fts.rowid rather than e.hash, which is not FTS5-indexable and degrades MATCH to a
    // full-corpus scan (docs/plans/2026-08-08-search-knn-perf.md §WP7).
    public const string FtsSnippetsForSurvivors = """
                                                  SELECT e.hash AS Hash, snippet(entries_fts, 0, '', '', '…', 12) AS Snippet
                                                  FROM entries_fts
                                                  JOIN entries e ON e.id = entries_fts.rowid
                                                  WHERE entries_fts MATCH @query AND entries_fts.rowid IN @ids
                                                  """;

    // vec0 modality: native KNN over the ctx partition replaces `WHERE {filter}` entirely, since the
    // partition already selects exactly the rows a search context can retrieve
    // (docs/plans/2026-08-08-search-knn-perf.md §3.1/§3.4). Row position is the RRF rank; `ORDER BY
    // v.distance, e.path` preserves the existing distance-tie break. Vector hits carry a fallback
    // snippet built in C# (the FTS list's snippet() wins for docs both modalities retrieve).
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

    // @scope null preserves memory_delete's documented reach ("wherever this hash lives",
    // including a shared row); a caller that enumerated one scope (the sweep, H2) passes it so
    // the delete cannot also remove a sibling row sharing this hash in another scope/workspace.
    public const string DeleteByHashAndProject =
        "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId AND (@scope IS NULL OR scope IS @scope)";

    // Sync propagates deletes through tombstones (FR-NM-8): the row's committed scope is
    // recorded before the delete so sync can suppress resurrection and ship the tombstone.
    public const string SelectScopeByHashAndProject =
        "SELECT scope FROM entries WHERE hash = @hash AND project_id = @projectId AND (@scope IS NULL OR scope IS @scope)";

    // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3): read alongside
    // SelectScopeByHashAndProject, before the delete, so the row's group can be recomputed afterward.
    public const string SelectDeleteRecomputeContext = """
                                                        SELECT scope AS Scope, context_label AS ContextLabel,
                                                               workspace_id AS WorkspaceId, source_file AS SourceFile
                                                        FROM entries
                                                        WHERE hash = @hash AND project_id = @projectId
                                                          AND (@scope IS NULL OR scope IS @scope)
                                                        """;

    public const string UpsertTombstone =
        "INSERT INTO sync_tombstones (hash, scope, deleted_at) VALUES (@hash, @scope, @deletedAt) " +
        "ON CONFLICT(hash, scope) DO UPDATE SET deleted_at = excluded.deleted_at";

    // Mirror delete/rename: removes committed chunks of the source path and its subtree (directory
    // delete cascades; workspace scratch is transient and stays), plus per-path watch fingerprints
    // so a delete-then-recreate cycle cannot hash-skip back to stale chunks. Watch registration survives.
    // Matching is on `path`, not `source_file`: mirror/ingest rows carry the real file path in both
    // columns, while manual memory_write rows carry path = <sha256(content)>.md and merely cite the
    // file in source_file — the digest owns the mirror rows, never manual rows that cite the file.
    public const string DeleteBySourcePath = """
                                             DELETE FROM entries
                                             WHERE project_id = @projectId AND workspace_id IS NULL
                                               AND (path = @path OR path LIKE @pathPrefix ESCAPE '\')
                                             """;

    // A replace deletes every chunk of the path and re-inserts them, so promotion_queue_entries_ad
    // (ADR-0023) fires even for chunks whose text is unchanged and which return under the same hash.
    // These three hold the candidates across that round trip; see ReplaceFileAsync.
    public const string CreateQueueRestoreTable = """
                                                  CREATE TEMP TABLE IF NOT EXISTS queue_restore (
                                                      project_id TEXT NOT NULL, hash TEXT NOT NULL, path TEXT NULL,
                                                      value TEXT NOT NULL, source_file TEXT NULL, score REAL NOT NULL,
                                                      reasons TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                                                  DELETE FROM queue_restore;
                                                  """;

    // e.scope = 'project' matches what ShareAsync can actually resolve
    // (SelectSourceByHashAndProject); a custom- or workspace-scoped row backing this hash cannot
    // promote it, so it must not count as "still backed" here either (H4).
    public const string CaptureQueueRowsForSourcePath = """
                                                        INSERT INTO queue_restore (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                                                        SELECT q.project_id, q.hash, q.path, q.value, q.source_file, q.score, q.reasons, q.created_at, q.updated_at
                                                        FROM promotion_queue q
                                                        WHERE q.project_id = @projectId
                                                          AND EXISTS (SELECT 1 FROM entries e
                                                                      WHERE e.project_id = q.project_id AND e.hash = q.hash
                                                                        AND e.scope = 'project'
                                                                        AND (e.path = @path OR e.path LIKE @pathPrefix ESCAPE '\'))
                                                        """;

    public const string RestoreQueueRowsStillBacked = """
                                                       INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                                                       SELECT r.project_id, r.hash, r.path, r.value, r.source_file, r.score, r.reasons, r.created_at, r.updated_at
                                                       FROM queue_restore r
                                                       WHERE EXISTS (SELECT 1 FROM entries e
                                                                     WHERE e.project_id = r.project_id AND e.hash = r.hash
                                                                       AND e.scope = 'project')
                                                         AND NOT EXISTS (SELECT 1 FROM promotion_discards d
                                                                         WHERE d.project_id = r.project_id AND d.hash = r.hash)
                                                       ON CONFLICT(project_id, hash) DO NOTHING;
                                                       DELETE FROM queue_restore;
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

    // Cross-process scan lease (docs/plans/2026-08-07-watch-scan-runaway-fix.md): the lease lives
    // on the watches row, not a separate table, so a row delete is lease release. ExecuteAsync(...)
    // == 1 tells the caller whether the grant/renewal succeeded.

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

    // Sets all four embed-transition columns together (docs/plans/2026-08-08-search-knn-perf.md
    // §3.6): a chunk with no heading writes heading_path = '' — never NULL, since NULL means "not
    // yet processed" and the vec_structure_au trigger guards on IS NOT NULL.
    public const string MarkEmbedded =
        "UPDATE entries SET embed_state = 'embedded', embedding = @embedding, " +
        "heading_path = @headingPath, structure_embedding = @structureEmbedding WHERE id = @id";

    // Structure-only counterpart to MarkEmbedded, for the healing pass: the row is already
    // embed_state='embedded', so only the structure columns move. The embed_state guard closes a
    // race with a concurrent sync reindex, which can clear the row back to 'pending' mid-heal.
    public const string MarkStructure =
        "UPDATE entries SET heading_path = @headingPath, structure_embedding = @structureEmbedding " +
        "WHERE id = @id AND embed_state = 'embedded'";

    // Healing candidates: embedded content with no structure yet (a bank embedded before the
    // structure writer landed, or a chunk whose heading never parsed). Bounded per call by @limit;
    // MarkStructure sets heading_path on every candidate touched, so it leaves this set for good.
    public const string SelectStructureHealCandidates =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'embedded' AND heading_path IS NULL " +
        "AND structure_embedding IS NULL AND project_id = @projectId ORDER BY id LIMIT @limit";

    public const string SelectEmbeddedForProject =
        "SELECT id AS Id, value AS Value FROM entries WHERE project_id = @projectId AND embed_state = 'embedded' " +
        "ORDER BY id";

    public const string SelectAllEmbedded =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'embedded' ORDER BY id";

    public const string CommittedContexts = """
                                            SELECT DISTINCT CASE WHEN scope = 'shared' THEN 'shared' ELSE 'project:' || project_id END AS context
                                            FROM entries
                                            WHERE scope = 'shared' OR (scope = 'project' AND project_id = @projectId)
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
        "SELECT created_at AS CreatedAt, access_count AS AccessCount FROM entries " +
        "WHERE hash = @hash AND (project_id = @projectId OR scope = 'shared') LIMIT 1";

    public const string BumpAccess =
        """
        UPDATE entries
        SET access_count = access_count + 1,
            last_accessed_at = @now,
            rating = @rating
        WHERE hash = @hash AND (project_id = @projectId OR scope = 'shared')
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

    /// <summary>The status guard's row plus its provenance, so an active-check reads it back for free.</summary>
    public const string SelectWorkspace =
        """
        SELECT status AS Status, agent_id AS AgentId, name AS Name
        FROM workspaces WHERE id = @workspaceId AND project_id = @projectId LIMIT 1
        """;

    public const string SelectSettingsByPrefix =
        """
        SELECT key, value FROM settings WHERE key LIKE @prefix || '%' ORDER BY key
        """;

    public const string DeleteSetting =
        """
        DELETE FROM settings WHERE key = @key
        """;

    // Scoped to the committed project row (H2): hash alone is not a unique row — a workspace or
    // custom-context write of identical content shares it — and only the project-scope row is
    // ever read by the sweep's degradation check, so a TTL on any other scope's sibling is inert.
    public const string UpdateEntryTtl =
        """
        UPDATE entries
        SET ttl_days = @ttlDays
        WHERE project_id = @projectId AND hash = @hash AND scope = 'project'
        """;

    // Scoped to the committed project row (H2), matching UpdateEntryTtl: both callers (the sweep's
    // degradation check, memory_set_ttl's response) mean the project row specifically, and without
    // this a same-hash sibling in another scope could supply the rating/ttl instead (LIMIT 1, no
    // ORDER BY, picks whichever row SQLite returns first).
    public const string SelectEntryMetadata =
        """
        SELECT rating AS Rating, ttl_days AS TtlDays
        FROM entries
        WHERE project_id = @projectId AND hash = @hash AND scope = 'project'
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
