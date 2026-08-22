using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL over our memory.db tables (see docs/work/archive/2026-08-03-native-memory-plan.md §2.2); kept in one place so the store stays thin.</summary>
internal static class MemorySql
{
    // ON CONFLICT DO NOTHING is bare — expression/partial indexes can't be a conflict target — so
    // concurrent same-bucket inserts converge; the loser re-reads by bucket key
    // (docs/work/archive/2026-08-06-extraction-followups-plan.md).
    // chunkIndex/totalChunks are written by the caller, not derived here (GH #371): a caller that
    // knows the row's real document position (FileIngestor) passes it directly; one that doesn't
    // passes -1 (docs/plans/2026-08-08-search-knn-perf.md §3.3 sentinel), leaving it for
    // RecomputeChunkColumnsForContext/BankWide to fill in.
    public const string InsertEntry = """
                                      INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                           workspace_id, agent_id, created_at, updated_at, source_id, chunk_index, total_chunks)
                                      VALUES (@hash, @path, @value, @sourceFile, @section, @scope, @projectId, @contextLabel,
                                              @workspaceId, @agentId, @createdAt, @updatedAt, @sourceId, @chunkIndex, @totalChunks)
                                      ON CONFLICT DO NOTHING
                                      """;

    public const string SelectEntryById = """
                                          SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                 project_id AS ProjectId, context_label AS ContextLabel,
                                                 workspace_id AS WorkspaceId, created_at AS CreatedAt
                                          FROM entries
                                          WHERE id = @id
                                          """;

    public static readonly string SelectSourceByHashAndProject = $""""""
                                                        SELECT e.path AS Path, e.value AS Value, e.source_file AS SourceFile, e.section AS Section,
                                                               ms.source_type AS SourceType, ms.heading_path AS HeadingPath
                                                       FROM entries e
                                                       LEFT JOIN memory_source ms ON ms.id = e.source_id
                                                       WHERE e.hash = @hash AND {ProjectRows.Of("e.")}
                                                       ORDER BY {ProjectRows.CommittedFirst("e.")}
                                                       LIMIT 1
                                                       """""";

    public static readonly string SelectExtractionCandidates = $""""""
                                                     SELECT e.hash AS Hash, e.path AS Path, e.value AS Value, e.source_file AS SourceFile,
                                                            ms.source_type AS SourceType,
                                                            e.rating AS Rating, e.access_count AS AccessCount, e.created_at AS CreatedAt,
                                                            e.ttl_days AS TtlDays
                                                     FROM entries e
                                                     LEFT JOIN memory_source ms ON ms.id = e.source_id
                                                     WHERE {ProjectRows.Of("e.")} AND e.embed_state = 'embedded'
                                                       AND (@includeTtlRows = 1 OR e.ttl_days IS NULL)
                                                     """""";

    public const string SelectSharedIndex = """"
                                            SELECT path AS Path, value AS Value
                                            FROM entries
                                            WHERE scope = 'shared'
                                            """";

    public static readonly string SelectProjectIds = $""""
                                           SELECT DISTINCT project_id AS ProjectId
                                           FROM entries
                                           WHERE {ProjectRows.Scope()}
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

    // bm25(entries_fts, 1.0, 8.0, 4.0) weights source_file/section matches 8x/4x a body-text
    // match, so identifier and section tokens outrank cross-referencing prose
    // (docs/plans/retrieval-improvement-c.md §3 2c). The section weight was 16 while the column
    // was empty for every ingested chunk; measured against a populated one, 4 beats both 16 and
    // NULL on exact-chunk retrieval (docs/adr/0044-section-weight.md). ChunkIndex/TotalChunks are persisted columns,
    // not a per-query window function (docs/plans/2026-08-08-search-knn-perf.md §3.2/§3.3).
    // snippet() is deferred to ranking survivors (docs/plans/2026-08-08-search-knn-perf.md §WP7) —
    // FtsSnippetsForSurvivors resolves it there instead.
    public const string SearchByFilter = """
                                         SELECT e.hash AS Hash, bm25(entries_fts, 1.0, 8.0, 4.0) AS Ranking,
                                                e.path AS Path, e.value AS Value, e.source_file AS SourceFile,
                                                e.chunk_index AS ChunkIndex, e.total_chunks AS TotalChunks,
                                                e.id AS Id
                                         FROM entries_fts
                                         JOIN entries e ON e.id = entries_fts.rowid
                                         WHERE entries_fts MATCH @query AND {filter}
                                         ORDER BY bm25(entries_fts, 1.0, 8.0, 4.0)
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
                                               SELECT e.hash AS Hash, e.path AS Path, e.value AS Value,
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
                                                        SELECT e.hash AS Hash, e.path AS Path, e.value AS Value,
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
                                                              workspace_id AS WorkspaceId, source_file AS SourceFile,
                                                              chunk_index AS ChunkIndex
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
    // These three hold the candidates across that round trip; see SqliteMemoryStore.ReplaceCoreAsync.
    public const string CreateQueueRestoreTable = """
                                                  CREATE TEMP TABLE IF NOT EXISTS queue_restore (
                                                      project_id TEXT NOT NULL, hash TEXT NOT NULL, path TEXT NULL,
                                                      value TEXT NOT NULL, source_file TEXT NULL, score REAL NOT NULL,
                                                      reasons TEXT NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
                                                  DELETE FROM queue_restore;
                                                  """;

    // "Backed" means the same rows ShareAsync can resolve — ProjectRows is the one definition
    // (ADR-0046); this used to be a hand-copied `e.scope = 'project'` in six places.
    public static readonly string CaptureQueueRowsForSourcePath = $"""
                                                        INSERT INTO queue_restore (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                                                        SELECT q.project_id, q.hash, q.path, q.value, q.source_file, q.score, q.reasons, q.created_at, q.updated_at
                                                        FROM promotion_queue q
                                                        WHERE q.project_id = @projectId
                                                          AND EXISTS (SELECT 1 FROM entries e
                                                                      WHERE e.project_id = q.project_id AND e.hash = q.hash
                                                                        AND {ProjectRows.Scope("e.")}
                                                                        AND (e.path = @path OR e.path LIKE @pathPrefix ESCAPE '\'))
                                                        """;

    public static readonly string RestoreQueueRowsStillBacked = $"""
                                                      INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                                                      SELECT r.project_id, r.hash, r.path, r.value, r.source_file, r.score, r.reasons, r.created_at, r.updated_at
                                                      FROM queue_restore r
                                                      WHERE EXISTS (SELECT 1 FROM entries e
                                                                    WHERE e.project_id = r.project_id AND e.hash = r.hash
                                                                      AND {ProjectRows.Scope("e.")})
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

    // Counts the project's contexts, labelled or not (ADR-0045) — PendingCount has always counted
    // every row carrying the project id, and a bank holding one context-labelled entry reported
    // `entries: 0, pending: 1` while the two disagreed about what "this project" meant.
    public const string CountProjectEntries =
        "SELECT count(*) FROM entries WHERE scope IN ('project', 'custom') AND project_id = @projectId";

    public const string PendingCount =
        "SELECT count(*) FROM entries WHERE embed_state = 'pending' AND project_id = @projectId";

    public const string SelectPendingForEmbed =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'pending' AND project_id = @projectId " +
        "ORDER BY id LIMIT @limit";

    /// <summary>Bank-wide pending-row existence check for PendingEmbedJob.HasWorkAsync, polled every 15s (BankMaintenanceHostedService.OnDemandPollInterval) — EXISTS short-circuits on the first row instead of counting the whole backlog on every poll.</summary>
    public const string HasPendingEmbed =
        "SELECT EXISTS(SELECT 1 FROM entries WHERE embed_state = 'pending' LIMIT 1)";

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

    /// <summary>Bank-wide (not project-scoped) pending rows — the migration relay's own drain, distinct from the per-project memory_embed_pending path.</summary>
    public const string SelectAllPendingForEmbed =
        "SELECT id AS Id, value AS Value FROM entries WHERE embed_state = 'pending' ORDER BY id LIMIT @limit";

    /// <summary>The outbox's own side effect: every currently-embedded row is stale under the new engine. Fires vec_entries_pending/vec_structure_pending, so the old vectors leave the searchable index the instant this commits.</summary>
    public const string MarkAllEmbeddedPending =
        "UPDATE entries SET embed_state = 'pending' WHERE embed_state = 'embedded'";

    /// <summary>The code corpus's own invalidation (§3.3 D-E9, no outbox): every currently-embedded code_entries
    /// row is stale under the new code engine. Fires vec_code_pending, so the old vectors leave vec_code the
    /// instant this commits — the code-reindex maintenance job re-embeds the pending rows, not this call.</summary>
    public const string MarkAllCodeEmbeddedPending =
        "UPDATE code_entries SET embed_state = 'pending' WHERE embed_state = 'embedded'";

    /// <summary>Bank-wide pending-row existence check for CodeReindexJob.HasWorkAsync — mirrors HasPendingEmbed.
    /// S2: excludes a row that crossed CodeCorpusSchema.MaxEmbedAttempts (the literal 3 below MUST track
    /// that constant — a const string can't interpolate a const int, so LoggerMessageEventIdTests-style
    /// drift protection isn't available here; MaxEmbedAttemptsSqlLiteralTests pins the two together instead),
    /// or a permanently-poisoned row would keep this (and therefore the 15s on-demand poll) true forever.</summary>
    public const string HasPendingCodeEmbed =
        "SELECT EXISTS(SELECT 1 FROM code_entries WHERE embed_state = 'pending' AND embed_attempts < 3 LIMIT 1)";

    /// <summary>Bank-wide (not project-scoped) pending code rows for the code-reindex drain — mirrors SelectAllPendingForEmbed.
    /// S2: the same MaxEmbedAttempts exclusion as HasPendingCodeEmbed (see its own remark on the literal 3) — a
    /// quarantined poison row is never re-selected.</summary>
    public const string SelectAllPendingCodeForEmbed =
        "SELECT id AS Id, value AS Value, path AS Path, source_file AS SourceFile FROM code_entries " +
        "WHERE embed_state = 'pending' AND embed_attempts < 3 " +
        "ORDER BY id LIMIT @limit";

    /// <summary>Fills the embedding column and flips embed_state — fires vec_code_au, which writes the row into vec_code.
    /// The @engine guard (S1) blocks the write when a concurrent activation changed embedding.codeEngine after this
    /// batch's rows were selected: without it, a stale UPDATE would mark the row 'embedded' with a vector generated
    /// under the PREVIOUS engine, permanently mismatched with the new engine's fingerprint. The row simply stays
    /// pending — the next drain re-embeds it under whichever engine is current then.</summary>
    public const string MarkCodeEmbedded =
        "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding " +
        "WHERE id = @id AND embed_state = 'pending' " +
        "AND (SELECT value FROM settings WHERE key = 'embedding.codeEngine') = @engine";

    /// <summary>S2: bumps a row's failure count after an individual (not whole-batch) embed attempt fails
    /// — the per-row fallback's own progress marker, so a poison row eventually crosses MaxEmbedAttempts
    /// and drops out of SelectAllPendingCodeForEmbed/HasPendingCodeEmbed instead of retrying forever.</summary>
    /// <remarks>RETURNING hands back the new count in the same statement, so the moment a row crosses the
    /// ceiling is knowable without a second read (#466: crossing it must be logged, not just recorded).</remarks>
    public const string IncrementCodeEmbedAttempts =
        "UPDATE code_entries SET embed_attempts = embed_attempts + 1 WHERE id = @id RETURNING embed_attempts";

    // ---- model_migration (ADR-0076) ----

    public const string SelectModelMigration =
        "SELECT provider AS Provider, model AS Model, base_url AS BaseUrl, engine AS Engine, " +
        "started_at AS StartedAt, finished_at AS FinishedAt FROM model_migration WHERE id = 1";

    public const string HasOpenModelMigration =
        "SELECT count(*) FROM model_migration WHERE id = 1 AND finished_at IS NULL";

    // The DO UPDATE's WHERE clause is the state-machine guard: Start only ever moves a closed (or
    // absent) row to open, never overwrites one already open. When the row is open, the WHERE is
    // false, SQLite treats the upsert as a no-op for it, and this affects 0 rows — the caller's
    // signal to refuse rather than clobber (mirrors ADR-0037's claim-by-update pattern).
    public const string StartModelMigration =
        """
        INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at)
        VALUES (1, @provider, @model, @baseUrl, @engine, @startedAt, NULL)
        ON CONFLICT(id) DO UPDATE SET
            provider = @provider, model = @model, base_url = @baseUrl, engine = @engine,
            started_at = @startedAt, finished_at = NULL, lease_owner = NULL, lease_expires_at = NULL
        WHERE model_migration.finished_at IS NOT NULL
        """;

    public const string FinishModelMigration =
        "UPDATE model_migration SET finished_at = @finishedAt, lease_owner = NULL, lease_expires_at = NULL " +
        "WHERE id = 1 AND finished_at IS NULL";

    public const string AcquireModelMigrationLease =
        "UPDATE model_migration SET lease_owner = @owner, lease_expires_at = @expiresAt " +
        "WHERE id = 1 AND finished_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < @now)";

    public const string RenewModelMigrationLease =
        "UPDATE model_migration SET lease_expires_at = @expiresAt " +
        "WHERE id = 1 AND finished_at IS NULL AND lease_owner = @owner";

    public const string ReleaseModelMigrationLease =
        "UPDATE model_migration SET lease_owner = NULL, lease_expires_at = NULL WHERE id = 1 AND lease_owner = @owner";

    // ---- repair_requests (ADR-0075 amendment) ----

    // ON CONFLICT resets finished_at to NULL unconditionally: a second request for a kind whose
    // first request already finished must reopen it; a second request while one is still open just
    // refreshes requested_at, which is harmless (the job re-derives its own report from a live scan).
    public const string RequestRepair =
        """
        INSERT INTO repair_requests (kind, requested_at, finished_at)
        VALUES (@kind, @requestedAt, NULL)
        ON CONFLICT(kind) DO UPDATE SET requested_at = @requestedAt, finished_at = NULL
        """;

    public const string HasOpenRepairRequest =
        "SELECT count(*) FROM repair_requests WHERE kind = @kind AND finished_at IS NULL";

    public const string FinishRepairRequest =
        "UPDATE repair_requests SET finished_at = @finishedAt WHERE kind = @kind AND finished_at IS NULL";

    // ---- promotion_queue_prune_requests (ADR-0075 amendment) ----

    // Same ON CONFLICT reasoning as RequestRepair: a second request after the first finished must
    // reopen it; a second request while one is still open just refreshes requested_at.
    public const string RequestPromotionQueuePrune =
        """
        INSERT INTO promotion_queue_prune_requests (id, requested_at, finished_at)
        VALUES (1, @requestedAt, NULL)
        ON CONFLICT(id) DO UPDATE SET requested_at = @requestedAt, finished_at = NULL
        """;

    public const string HasOpenPromotionQueuePruneRequest =
        "SELECT count(*) FROM promotion_queue_prune_requests WHERE id = 1 AND finished_at IS NULL";

    public const string FinishPromotionQueuePruneRequest =
        "UPDATE promotion_queue_prune_requests SET finished_at = @finishedAt WHERE id = 1 AND finished_at IS NULL";

    // Custom contexts are listed alongside shared/project because their label is the only key that
    // reaches their rows through memory_search (SearchContexts.For), and this is the only place a
    // caller can read it back. Omitting them made a context-scoped write unreachable by any means
    // except a hash the caller had to have kept.
    /// <summary>The custom-context labels a project has rows under; the project scope reads them all.</summary>
    public const string CustomContextLabels = """
                                              SELECT DISTINCT context_label
                                              FROM entries
                                              WHERE scope = 'custom' AND project_id = @projectId
                                                AND context_label IS NOT NULL
                                              ORDER BY context_label
                                              """;

    public static readonly string CommittedContexts = $"""
                                            SELECT DISTINCT CASE
                                                     WHEN scope = 'shared' THEN 'shared'
                                                     WHEN scope = 'custom' THEN 'custom:' || context_label
                                                     ELSE 'project:' || project_id
                                                   END AS context
                                            FROM entries
                                            WHERE scope = 'shared' OR ({ProjectRows.Of()})
                                            ORDER BY CASE WHEN scope = 'shared' THEN 0 WHEN scope = 'project' THEN 1 ELSE 2 END, context
                                            """;

    public const string DistinctFilePaths = """
                                            SELECT DISTINCT path
                                            FROM entries
                                            WHERE scope IN ('shared', 'project')
                                              AND (project_id = @projectId OR scope = 'shared')
                                              AND path IS NOT NULL
                                            ORDER BY path
                                            """;

    // memory_get read path (ADR-0035): a hash addressable within the caller's own project rows
    // plus the cross-project shared tier — the same reach BumpAccess already
    // grant search hits.
    public const string SelectEntryByHashForRead = """
                                                   SELECT hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                          project_id AS ProjectId, context_label AS ContextLabel,
                                                          workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                   FROM entries
                                                   WHERE hash = @hash AND (project_id = @projectId OR scope = 'shared')
                                                   LIMIT 1
                                                   """;

    /// <summary>
    ///     One statement, so `rating` is always the rating of the `access_count` stored beside it.
    ///     Computing it in C# from an earlier SELECT lost a bump's rating whenever two hits on one
    ///     hash interleaved — the counter is relative and survives, a literal rating does not
    ///     (docs/adr/0053). SQLite evaluates every SET right-hand side against the pre-UPDATE row, so
    ///     `access_count + 1` here is the new count. Mirrors RatingPolicy.Rating.
    /// </summary>
    public const string BumpAccess =
        """
        UPDATE entries
        SET access_count = access_count + 1,
            last_accessed_at = @now,
            rating = @baseScore
                     * pow(0.5, max(0.0, (@now - created_at) / 86400.0) / @halfLifeDays)
                     * (1 + (access_count + 1) * @accessMultiplier)
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

    // Hash alone is not a unique row — identical content under a different label shares one — so
    // the committed row wins when both exist (ProjectRows.CommittedFirst). It used to be restricted
    // to scope='project', which turned that tie-break into an existence test: an entry that lived
    // only in a context was reported unknown-hash (ADR-0046).
    public static readonly string UpdateEntryTtl =
        $"""
        UPDATE entries
        SET ttl_days = @ttlDays
        WHERE id = (SELECT id FROM entries
                    WHERE hash = @hash AND {ProjectRows.Of()}
                    ORDER BY {ProjectRows.CommittedFirst()}, id
                    LIMIT 1)
        """;

    // Reads the same row UpdateEntryTtl writes — the explicit ORDER BY is what makes "the entry
    // for this hash" deterministic when a labelled sibling shares it (ADR-0046).
    public static readonly string SelectEntryMetadata =
        $"""
        SELECT rating AS Rating, ttl_days AS TtlDays
        FROM entries
        WHERE hash = @hash AND {ProjectRows.Of()}
        ORDER BY {ProjectRows.CommittedFirst()}, id
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

    /// <summary>
    ///     The write path's post-insert lookup. Scoped by hash as well as path because a chunked
    ///     `memory_write` puts several rows under one path (docs/adr/0064), and the entry the caller
    ///     is handed back must be the chunk whose hash it was told — not whichever row LIMIT 1 picks.
    /// </summary>
    public const string SelectEntryByPathAndHashInBucket = """
                                                           SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                                  project_id AS ProjectId, context_label AS ContextLabel,
                                                                  workspace_id AS WorkspaceId, created_at AS CreatedAt
                                                           FROM entries
                                                           WHERE path = @path AND hash = @hash AND scope IS @scope
                                                             AND project_id = @projectId
                                                             AND context_label IS @contextLabel AND workspace_id IS @workspaceId
                                                           LIMIT 1
                                                           """;

    // Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3, GH #371): total_chunks
    // is a pure count, always safe to refresh; chunk_index is filled ONLY where it is still the -1
    // "unknown" sentinel (id order is a best-effort fallback for a row nothing has positioned yet) —
    // a row an authoritative writer (FileIngestor) already gave a real, document-order position is
    // never touched, so this can run after that writer without undoing its work.
    public static readonly string RecomputeChunkColumnsForContext = $"""
                                                                     WITH grp AS (
                                                                         SELECT id, chunk_index,
                                                                                ROW_NUMBER() OVER (PARTITION BY {ContextKeyExpression("")}, source_file ORDER BY id) - 1 AS ci,
                                                                                COUNT(*)     OVER (PARTITION BY {ContextKeyExpression("")}, source_file)              AS tc
                                                                         FROM entries
                                                                         WHERE source_file IS NOT NULL AND ({ContextKeyExpression("")}) = @ctx AND source_file = @sourceFile)
                                                                     UPDATE entries
                                                                        SET total_chunks = (SELECT tc FROM grp g WHERE g.id = entries.id),
                                                                            chunk_index  = CASE WHEN entries.chunk_index < 0
                                                                                                 THEN (SELECT ci FROM grp g WHERE g.id = entries.id)
                                                                                                 ELSE entries.chunk_index END
                                                                      WHERE entries.id IN (SELECT id FROM grp)
                                                                     """;

    /// <summary>Bank-wide form of <see cref="RecomputeChunkColumnsForContext" /> — no ctx/source_file predicate, used by the v2/v3 migrations and the chunk-oversize backfill (GH #371: both can re-run on a live bank next to groups a document-order write already positioned correctly, so this fills the -1 sentinel only, same as the scoped form).</summary>
    public static readonly string RecomputeChunkColumnsBankWide = $"""
                                                                   WITH grp AS (
                                                                       SELECT id, chunk_index,
                                                                              ROW_NUMBER() OVER (PARTITION BY {ContextKeyExpression("")}, source_file ORDER BY id) - 1 AS ci,
                                                                              COUNT(*)     OVER (PARTITION BY {ContextKeyExpression("")}, source_file)              AS tc
                                                                       FROM entries
                                                                       WHERE source_file IS NOT NULL)
                                                                   UPDATE entries
                                                                      SET total_chunks = (SELECT tc FROM grp g WHERE g.id = entries.id),
                                                                          chunk_index  = CASE WHEN entries.chunk_index < 0
                                                                                               THEN (SELECT ci FROM grp g WHERE g.id = entries.id)
                                                                                               ELSE entries.chunk_index END
                                                                    WHERE entries.id IN (SELECT id FROM grp)
                                                                   """;

    /// <summary>
    ///     The pre-GH-#371 bank-wide recompute, unconditional id order — kept, under its own name,
    ///     for <see cref="AiRaccoon.Infrastructure.Sync.SyncService" />'s post-merge pass alone. A
    ///     merge pulls rows from a second bank whose row ids carry no relationship to this bank's, so
    ///     there is no position to protect by skipping already-assigned rows the way the other
    ///     bank-wide form does; a tombstone-driven delete needs the SAME survivors renumbered.
    ///     Explicit `memory_sync`, not something <c>MaintenanceJobRunner</c> can start unattended, so
    ///     it does not carry the "must never run within seconds of bank open" risk the sentinel-guarded
    ///     forms exist for. Re-deriving real document order across a sync is a separate design
    ///     question (transmitting position with the row) — out of scope here.
    /// </summary>
    public static readonly string RecomputeChunkColumnsBankWideFromIdOrder = $"""
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

    /// <summary>Sets one row's document position directly — the authoritative-at-insert write (GH #371) FileIngestor uses instead of the id-order recompute.</summary>
    public const string SetChunkPosition =
        "UPDATE entries SET chunk_index = @chunkIndex, total_chunks = @totalChunks WHERE id = @id";

    // Renumbers survivors after a row is removed from a (ctx, source_file) group: shifts every
    // later chunk_index down by one and shrinks total_chunks — never re-derives from id order, so it
    // cannot scramble a group whose positions are already correct. @deletedIndex < 0 means the
    // removed row's own position was unknown, so nothing shifts (only the count shrinks).
    public static readonly string CompactChunkColumnsAfterDelete = $"""
                                                                     UPDATE entries
                                                                        SET chunk_index = CASE WHEN @deletedIndex >= 0 AND chunk_index > @deletedIndex
                                                                                                THEN chunk_index - 1 ELSE chunk_index END,
                                                                            total_chunks = MAX(total_chunks - 1, 0)
                                                                      WHERE source_file IS NOT NULL AND ({ContextKeyExpression("")}) = @ctx AND source_file = @sourceFile
                                                                     """;

    // The vec0 `ctx` column — a partition key until v9, a metadata column since (ADR-0068).
    // Length-prefixed, not
    // ':'-joined, because the naive join collides across project id/label boundaries.
    public static string ContextKeyExpression(string prefix) =>
        $"""
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

    // Code corpus (docs/work/2026-08-21-code-search-implementation-plan.md §3.4): dedup key is
    // (project_id, path, hash) — no bucket (scope/context_label/workspace_id) columns, code is
    // project-scoped only. Same ON CONFLICT DO NOTHING + post-conflict re-read shape as
    // InsertEntry/SelectChunkIdByPathAndHashInBucket above.
    public const string InsertCodeEntry = """
                                          INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end,
                                                                    project_id, created_at, updated_at, chunk_index, total_chunks)
                                          VALUES (@hash, @path, @value, @sourceFile, @lineStart, @lineEnd,
                                                  @projectId, @createdAt, @updatedAt, @chunkIndex, @totalChunks)
                                          ON CONFLICT DO NOTHING
                                          """;

    public const string SelectCodeChunkIdByPathAndHash = """
                                                         SELECT id FROM code_entries
                                                         WHERE project_id = @projectId AND path = @path AND hash = @hash
                                                         LIMIT 1
                                                         """;

    // D-E11 (deliberate divergence from the memory path): dedup rediscovery refreshes the line
    // range too, not just chunk_index/total_chunks — for code, the line range IS the retrieval
    // payload, so a file that gains lines above an unchanged chunk must not keep a stale range.
    public const string UpdateCodeChunkPosition = """
                                                  UPDATE code_entries
                                                     SET line_start = @lineStart, line_end = @lineEnd,
                                                         chunk_index = @chunkIndex, total_chunks = @totalChunks,
                                                         updated_at = @updatedAt
                                                   WHERE id = @id
                                                  """;

    public const string DeleteCodeBySourcePath = """
                                                 DELETE FROM code_entries
                                                 WHERE project_id = @projectId
                                                   AND (path = @path OR path LIKE @pathPrefix ESCAPE '\')
                                                 """;

    // Defect B: after a direct ingest reports the chunk set it wrote or rediscovered, everything
    // else stored under that exact path is a leftover of a previous chunking. Exact path only —
    // no subtree leg — so re-ingesting one file can never reach a sibling.
    public const string DeleteChunksForPathExcept = """
                                                    DELETE FROM entries
                                                    WHERE project_id = @projectId AND workspace_id IS NULL
                                                      AND path = @path AND hash NOT IN @keep
                                                    """;

    public const string DeleteAllChunksForPath = """
                                                 DELETE FROM entries
                                                 WHERE project_id = @projectId AND workspace_id IS NULL
                                                   AND path = @path
                                                 """;

    // #436, code-corpus leg of Defect B: same predicate as DeleteCodeBySourcePath (path or
    // subtree prefix) — inert for a single file, since no sibling file's path can equal
    // "<this file>/…", so a single-file re-ingest cannot reach a sibling.
    public const string DeleteCodeChunksForPathExcept = """
                                                        DELETE FROM code_entries
                                                        WHERE project_id = @projectId
                                                          AND (path = @path OR path LIKE @pathPrefix ESCAPE '\')
                                                          AND hash NOT IN @keep
                                                        """;

    public const string DeleteAllCodeChunksForPath = """
                                                     DELETE FROM code_entries
                                                     WHERE project_id = @projectId
                                                       AND (path = @path OR path LIKE @pathPrefix ESCAPE '\')
                                                     """;
}
