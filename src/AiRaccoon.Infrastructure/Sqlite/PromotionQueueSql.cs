namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL for the promotion_queue table — kept out of MemorySql so the entries layer stays bounded.</summary>
internal static class PromotionQueueSql
{
    public const string Upsert = """
                                 INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, created_at, updated_at)
                                 VALUES (@ProjectId, @Hash, @Path, @Value, @SourceFile, @Score, @Reasons, @CreatedAt, @UpdatedAt)
                                 ON CONFLICT(project_id, hash) DO UPDATE SET
                                     path = excluded.path,
                                     value = excluded.value,
                                     source_file = excluded.source_file,
                                     score = excluded.score,
                                     reasons = excluded.reasons,
                                     updated_at = excluded.updated_at
                                 """;

    /// <summary>Hashes already queued for this project, restricted to the candidate batch — the pre-upsert snapshot UpsertAsync diffs against to report a genuine insert count.</summary>
    public const string ExistingHashes = """
                                         SELECT hash FROM promotion_queue
                                         WHERE project_id = @ProjectId AND hash IN @Hashes
                                         """;

    public const string List = """
                               SELECT project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                      source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                      created_at AS CreatedAt, updated_at AS UpdatedAt
                               FROM promotion_queue
                               WHERE (@ProjectId IS NULL OR project_id = @ProjectId)
                               ORDER BY score DESC, created_at ASC, id ASC
                               """;

    public const string Discard = """
                                  DELETE FROM promotion_queue
                                  WHERE project_id = @ProjectId AND (@Hash IS NULL OR hash = @Hash)
                                  RETURNING project_id AS ProjectId, hash AS Hash, path AS Path, value AS Value,
                                            source_file AS SourceFile, score AS Score, reasons AS Reasons,
                                            created_at AS CreatedAt, updated_at AS UpdatedAt
                                  """;

    public const string StatsPerProject = """
                                          SELECT project_id AS ProjectId, count(*) AS Count
                                          FROM promotion_queue
                                          GROUP BY project_id
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
                                                created_at AS CreatedAt, updated_at AS UpdatedAt
                                      """;
}
