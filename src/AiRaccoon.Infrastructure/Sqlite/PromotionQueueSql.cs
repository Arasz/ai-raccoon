namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL for the promotion_queue table — kept out of MemorySql so the entries layer stays bounded.</summary>
internal static class PromotionQueueSql
{
    public const string Upsert = """
                                 INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, scorer_version, created_at, updated_at)
                                 SELECT @ProjectId, @Hash, @Path, @Value, @SourceFile, @Score, @Reasons, @ScorerVersion, @CreatedAt, @UpdatedAt
                                 WHERE NOT EXISTS (SELECT 1 FROM promotion_discards d
                                                   WHERE d.project_id = @ProjectId AND d.hash = @Hash)
                                   AND NOT EXISTS (SELECT 1 FROM entries e
                                                   WHERE e.scope = 'shared' AND e.value = @Value)
                                 ON CONFLICT(project_id, hash) DO UPDATE SET
                                     path = excluded.path,
                                     value = excluded.value,
                                     source_file = excluded.source_file,
                                     score = excluded.score,
                                     reasons = excluded.reasons,
                                     scorer_version = excluded.scorer_version,
                                     updated_at = excluded.updated_at
                                 """;

    /// <summary>One agent rejection (docs/adr/0026): permanent per (project_id, hash), idempotent.</summary>
    public const string RememberDiscard = """
                                          INSERT OR IGNORE INTO promotion_discards (project_id, hash, discarded_at)
                                          VALUES (@ProjectId, @Hash, @DiscardedAt)
                                          """;

    /// <summary>
    ///     Residue sweep (docs/adr/0026): queued rows that are already shared (exact value
    ///     twin) or were rejected by a prior discard leave the queue.
    /// </summary>
    public const string PruneRejected = """
                                        DELETE FROM promotion_queue
                                        WHERE project_id = @ProjectId
                                          AND (EXISTS (SELECT 1 FROM entries e
                                                       WHERE e.scope = 'shared' AND e.value = promotion_queue.value)
                                               OR EXISTS (SELECT 1 FROM promotion_discards d
                                                          WHERE d.project_id = promotion_queue.project_id
                                                            AND d.hash = promotion_queue.hash))
                                        """;

    /// <summary>Hashes already queued for this project, restricted to the candidate batch — the pre-upsert snapshot UpsertAsync diffs against to report a genuine insert count.</summary>
    public const string ExistingHashes = """
                                         SELECT hash FROM promotion_queue
                                         WHERE project_id = @ProjectId AND hash IN @Hashes
                                         """;

    public const string List = """
                               SELECT project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                      source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                      created_at AS CreatedAt, updated_at AS UpdatedAt, scorer_version AS ScorerVersion
                               FROM promotion_queue
                               WHERE (@ProjectId IS NULL OR project_id = @ProjectId)
                               ORDER BY score DESC, created_at ASC, id ASC
                               """;

    public const string Discard = """
                                  DELETE FROM promotion_queue
                                  WHERE project_id = @ProjectId AND (@Hash IS NULL OR hash = @Hash)
                                  RETURNING project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                            source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                            created_at AS CreatedAt, updated_at AS UpdatedAt, scorer_version AS ScorerVersion
                                  """;

    public const string StatsPerProject = """
                                          SELECT project_id AS ProjectId, count(*) AS Count
                                          FROM promotion_queue
                                          GROUP BY project_id
                                          """;

    /// <summary>One project's waiting count and wait ages (whole bank when @ProjectId is null), with the occupying-project count the capacity split needs.</summary>
    public const string WaitStats = """
                                    SELECT count(*) AS WaitingCount,
                                           CAST(avg(@Now - created_at) AS REAL) AS AvgWaitSeconds,
                                           CAST(max(@Now - created_at) AS REAL) AS OldestWaitSeconds,
                                           (SELECT count(DISTINCT project_id) FROM promotion_queue) AS OccupyingProjects
                                    FROM promotion_queue
                                    WHERE (@ProjectId IS NULL OR project_id = @ProjectId)
                                    """;

    // e.scope = 'project' matches what ShareAsync can actually resolve
    // (MemorySql.SelectSourceByHashAndProject) — the same alignment as the
    // promotion_queue_entries_ad trigger's guard (H4/H5): a custom- or workspace-scoped sibling
    // cannot back a promotable candidate, so it must not read as "not an orphan" here either.
    /// <summary>Orphans pre-dating the promotion_queue_entries_ad trigger (ADR-0023): rows whose (project_id, hash) has no backing project-scope entries row, grouped per project for the dry-run report.</summary>
    public const string OrphanCountsPerProject = """
                                                 SELECT project_id AS ProjectId, count(*) AS Count
                                                 FROM promotion_queue q
                                                 WHERE NOT EXISTS (SELECT 1 FROM entries e
                                                                   WHERE e.project_id = q.project_id AND e.hash = q.hash
                                                                     AND e.scope = 'project')
                                                 GROUP BY project_id
                                                 """;

    public const string DeleteOrphans = """
                                        DELETE FROM promotion_queue
                                        WHERE NOT EXISTS (SELECT 1 FROM entries e
                                                          WHERE e.project_id = promotion_queue.project_id
                                                            AND e.hash = promotion_queue.hash
                                                            AND e.scope = 'project')
                                        """;

    public const string EvictVictim = """
                                      DELETE FROM promotion_queue
                                      WHERE id = (
                                          SELECT id FROM promotion_queue
                                          WHERE project_id = @ProjectId
                                          ORDER BY score ASC, created_at ASC, id ASC
                                          LIMIT 1
                                      )
                                      RETURNING project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                                source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                                created_at AS CreatedAt, updated_at AS UpdatedAt, scorer_version AS ScorerVersion
                                      """;

    /// <summary>The auto-clear for a retired scorer (ADR-0018): a project's queued rows not carrying the current scorer_version are gone.</summary>
    public const string ClearStale = """
                                     DELETE FROM promotion_queue
                                     WHERE project_id = @ProjectId AND scorer_version != @CurrentVersion
                                     """;
}
