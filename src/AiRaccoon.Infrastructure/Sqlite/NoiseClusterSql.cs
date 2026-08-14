namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL for noise_clusters/vec_noise (ADR-0039) — kept out of MemorySql so the entries layer stays bounded.</summary>
internal static class NoiseClusterSql
{
    /// <summary>`user_id IS @UserId` is NULL-safe both ways: matches a NULL column against a NULL parameter and an exact value against a non-NULL one.</summary>
    public const string SelectByProjectAndUser = """
                                                 SELECT id AS Id, project_id AS ProjectId, user_id AS UserId, cluster_label AS ClusterLabel,
                                                        sample_content AS SampleContent, frequency AS Frequency, status AS Status,
                                                        centroid_embedding AS CentroidEmbeddingBlob
                                                 FROM noise_clusters
                                                 WHERE project_id = @ProjectId AND user_id IS @UserId
                                                 """;

    /// <summary>Upsert keyed on the UNIQUE(project_id, cluster_label) constraint — a fresh candidate always carries a newly generated label, so this only ever updates the row OnlineNoiseClusteringService just read.</summary>
    public const string Upsert = """
                                 INSERT INTO noise_clusters (project_id, user_id, cluster_label, sample_content, frequency, status, centroid_embedding, created_at, last_seen_at)
                                 VALUES (@ProjectId, @UserId, @ClusterLabel, @SampleContent, @Frequency, @Status, @CentroidEmbeddingBlob, @Now, @Now)
                                 ON CONFLICT(project_id, cluster_label) DO UPDATE SET
                                     frequency = excluded.frequency,
                                     status = excluded.status,
                                     sample_content = excluded.sample_content,
                                     centroid_embedding = excluded.centroid_embedding,
                                     last_seen_at = excluded.last_seen_at
                                 RETURNING id
                                 """;

    /// <summary>vec_noise is a write-only mirror today (GetClustersAsync brute-force scans noise_clusters, which is correct at the small per-project cluster counts this subsystem expects) — kept in sync for future KNN-based lookup, per ADR-0039.</summary>
    public const string DeleteVecNoiseRow = "DELETE FROM vec_noise WHERE rowid = @Id";

    public const string InsertVecNoiseRow = "INSERT INTO vec_noise(rowid, ctx, embedding) VALUES (@Id, @Ctx, @Embedding)";
}
