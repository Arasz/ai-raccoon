namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL over our memory.db tables (see docs/work/2026-08-03-native-memory-plan.md §2.2); kept in one place so the store stays thin.</summary>
internal static class MemorySql
{
    // embed_state defaults to 'pending': every write lands deferred until the embed pipeline runs.
    public const string InsertEntry = """
                                      INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                                                           workspace_id, agent_id, created_at, updated_at)
                                      VALUES (@hash, @path, @value, @sourceFile, @section, @scope, @projectId, @contextLabel,
                                              @workspaceId, @agentId, @createdAt, @updatedAt)
                                      """;

    public const string SelectEntryById = """
                                           SELECT id AS Id, hash AS Hash, path AS Path, value AS Value, scope AS Scope,
                                                  project_id AS ProjectId, context_label AS ContextLabel,
                                                  workspace_id AS WorkspaceId, created_at AS CreatedAt
                                           FROM entries
                                           WHERE id = @id
                                           """;

    public const string SelectSourceByHashAndProject = """
                                                         SELECT path AS Path, value AS Value
                                                        FROM entries
                                                        WHERE hash = @hash AND scope = 'project' AND project_id = @projectId
                                                        LIMIT 1
                                                        """;

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
    // prose. ChunkIndex/TotalChunks are computed per source_file partition (0 for rows
    // without a source); the window functions live in a MATERIALIZED CTE because FTS5's
    // bm25() cannot share a SELECT with a window function and SQLite re-executes an inlined
    // window subquery once per FTS row (O(n²) on the corpus).
    public const string SearchByFilter = """
                                         WITH candidates AS MATERIALIZED (
                                             SELECT e.id AS Id, e.hash AS Hash, e.path AS Path, e.value AS Value,
                                                    e.source_file AS SourceFile,
                                                    CASE WHEN e.source_file IS NULL THEN 0
                                                         ELSE ROW_NUMBER() OVER (PARTITION BY e.source_file ORDER BY e.id) - 1 END AS ChunkIndex,
                                                    CASE WHEN e.source_file IS NULL THEN 0
                                                         ELSE COUNT(*) OVER (PARTITION BY e.source_file) END AS TotalChunks
                                             FROM entries e
                                             WHERE {filter}
                                         )
                                         SELECT c.Hash, 0 AS Seq, bm25(entries_fts, 1.0, 8.0, 16.0) AS Ranking,
                                                c.Path, snippet(entries_fts, 0, '', '', '…', 12) AS Snippet,
                                                c.Value, c.SourceFile, c.ChunkIndex, c.TotalChunks
                                         FROM entries_fts
                                         JOIN candidates c ON c.Id = entries_fts.rowid
                                         WHERE entries_fts MATCH @query
                                         ORDER BY bm25(entries_fts, 1.0, 8.0, 16.0)
                                         LIMIT @limit
                                         """;

    // vec0 modality: cosine KNN over the embedded rows, ordered by distance ascending so
    // the row position is the rank for RRF. Vector hits carry a fallback snippet built in
    // C# from the entry value (the FTS list's snippet() payload wins for docs both
    // modalities retrieve). The content list feeds the dual-vector fusion.
    public const string VectorSearchByFilter = """
                                                SELECT e.hash AS Hash, 0 AS Seq, e.path AS Path,
                                                       e.value AS Value,
                                                       vec_distance_cosine(v.embedding, @queryVector) AS Distance,
                                                       e.source_file AS SourceFile,
                                                       CASE WHEN e.source_file IS NULL THEN 0
                                                            ELSE ROW_NUMBER() OVER (PARTITION BY e.source_file ORDER BY e.id) - 1 END AS ChunkIndex,
                                                       CASE WHEN e.source_file IS NULL THEN 0
                                                            ELSE COUNT(*) OVER (PARTITION BY e.source_file) END AS TotalChunks
                                                FROM vec_entries v
                                                JOIN entries e ON e.id = v.rowid
                                                WHERE {filter}
                                                ORDER BY vec_distance_cosine(v.embedding, @queryVector), e.path
                                                LIMIT @limit
                                                """;

    // Structure modality: cosine KNN over the heading-path vectors (vec_structure),
    // same shape as the content query (source identity included) so both lists fuse in C# by
    // entry hash.
    public const string StructureVectorSearchByFilter = """
                                                         SELECT e.hash AS Hash, 0 AS Seq, e.path AS Path,
                                                                e.value AS Value,
                                                                vec_distance_cosine(v.embedding, @queryVector) AS Distance,
                                                                e.source_file AS SourceFile,
                                                                CASE WHEN e.source_file IS NULL THEN 0
                                                                     ELSE ROW_NUMBER() OVER (PARTITION BY e.source_file ORDER BY e.id) - 1 END AS ChunkIndex,
                                                                CASE WHEN e.source_file IS NULL THEN 0
                                                                     ELSE COUNT(*) OVER (PARTITION BY e.source_file) END AS TotalChunks
                                                         FROM vec_structure v
                                                         JOIN entries e ON e.id = v.rowid
                                                         WHERE {filter}
                                                         ORDER BY vec_distance_cosine(v.embedding, @queryVector), e.path
                                                         LIMIT @limit
                                                         """;

    public const string DeleteByHashAndProject =
        "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId";

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
