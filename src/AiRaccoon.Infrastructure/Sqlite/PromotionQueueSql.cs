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

    /// <summary>
    ///     Both conditions on purpose: age alone would forget a rejection whose entry is still in the
    ///     bank, and the propose path would offer it again (ADR-0055).
    /// </summary>
    public const string PurgeOldDiscards = """
                                           DELETE FROM promotion_discards
                                           WHERE discarded_at < @cutoff
                                             AND NOT EXISTS (SELECT 1 FROM entries e WHERE e.hash = promotion_discards.hash)
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

    /// <summary>Orphans pre-dating the promotion_queue_entries_ad trigger (ADR-0023): rows whose (project_id, hash) has no backing entries row inside the project (<see cref="ProjectRows" />, ADR-0046), grouped per project for the dry-run report.</summary>
    public static readonly string OrphanCountsPerProject = $"""
                                                            SELECT project_id AS ProjectId, count(*) AS Count
                                                            FROM promotion_queue q
                                                            WHERE NOT EXISTS (SELECT 1 FROM entries e
                                                                              WHERE e.project_id = q.project_id AND e.hash = q.hash
                                                                                AND {ProjectRows.Scope("e.")})
                                                            GROUP BY project_id
                                                            """;

    public static readonly string DeleteOrphans = $"""
                                                   DELETE FROM promotion_queue
                                                   WHERE NOT EXISTS (SELECT 1 FROM entries e
                                                                     WHERE e.project_id = promotion_queue.project_id
                                                                       AND e.hash = promotion_queue.hash
                                                                       AND {ProjectRows.Scope("e.")})
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

    /// <summary>Claims one row by marking it claimed (WP5b/A-F11) rather than deleting it — a
    /// ShareAsync failure after this leaves the row reclaimable instead of destroying the
    /// candidate. Affects 0 rows when already claimed or gone.</summary>
    public const string Claim = """
                                UPDATE promotion_queue
                                SET claimed_at = @ClaimedAt
                                WHERE project_id = @ProjectId AND hash = @Hash AND claimed_at IS NULL
                                RETURNING project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                          source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                          created_at AS CreatedAt, updated_at AS UpdatedAt, scorer_version AS ScorerVersion
                                """;

    /// <summary>Releases claims older than the stale threshold back to the queue (WP5b/A-F11): the
    /// caller that claimed a row and then died or hung mid-ShareAsync never gets to unclaim it
    /// itself. Returns the count released.</summary>
    public const string ReclaimStaleClaims = """
                                             UPDATE promotion_queue
                                             SET claimed_at = NULL
                                             WHERE claimed_at IS NOT NULL AND claimed_at < @CutoffAt
                                             """;
}
