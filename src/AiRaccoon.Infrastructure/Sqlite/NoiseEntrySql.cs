namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL for the noise_entries table — kept out of MemorySql so the entries layer stays bounded.</summary>
internal static class NoiseEntrySql
{
    public const string Insert = """
                                 INSERT INTO noise_entries (request_content, project_id, source_file, detected_by_policy, expires_at, created_at)
                                 VALUES (@RequestContent, @ProjectId, @SourceFile, @DetectedByPolicy, @ExpiresAt, @CreatedAt)
                                 """;

    public const string CountByPolicy = "SELECT detected_by_policy AS Policy, count(*) AS Count FROM noise_entries GROUP BY detected_by_policy";

    public const string SelectRecent = """
                                       SELECT id AS Id, request_content AS RequestContent, project_id AS ProjectId,
                                              source_file AS SourceFile, detected_by_policy AS DetectedByPolicy,
                                              expires_at AS ExpiresAt, created_at AS CreatedAt
                                       FROM noise_entries
                                       ORDER BY created_at DESC
                                       LIMIT @Limit
                                       """;

    public const string DeleteExpired = "DELETE FROM noise_entries WHERE expires_at <= @Now";
}
