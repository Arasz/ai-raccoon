namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL over our memory.db tables (plan §2.2); kept in one place so the store stays thin.</summary>
internal static class MemorySql
{
    // embed_state defaults to 'pending': embeddings are P4 — every write lands deferred.
    public const string InsertEntry = """
                                      INSERT INTO entries (hash, path, value, scope, project_id, context_label,
                                                           workspace_id, agent_id, created_at, updated_at)
                                      VALUES (@hash, @path, @value, @scope, @projectId, @contextLabel,
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

    // Global content dedup (FR-NM-7): the earliest committed row (workspace_id IS NULL) holding
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

    public const string SearchByFilter = """
                                         SELECT e.hash AS Hash, 0 AS Seq, bm25(entries_fts) AS Ranking,
                                                e.path AS Path, snippet(entries_fts, 0, '', '', '…', 12) AS Snippet
                                         FROM entries_fts
                                         JOIN entries e ON e.id = entries_fts.rowid
                                         WHERE entries_fts MATCH @query
                                           AND {filter}
                                         ORDER BY bm25(entries_fts)
                                         LIMIT @limit
                                         """;

    public const string DeleteByHashAndProject =
        "DELETE FROM entries WHERE hash = @hash AND project_id = @projectId";

    public const string CountProjectEntries =
        "SELECT count(*) FROM entries WHERE scope = 'project' AND project_id = @projectId";

    public const string PendingCount =
        "SELECT count(*) FROM entries WHERE embed_state = 'pending' AND project_id = @projectId";

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

    public const string UpsertSetting = """
                                        INSERT INTO settings (key, value) VALUES (@key, @value)
                                        ON CONFLICT(key) DO UPDATE SET value = excluded.value
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
