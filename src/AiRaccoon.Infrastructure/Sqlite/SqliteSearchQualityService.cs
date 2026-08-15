using System.Text.Json;
using AiRaccoon.Core.SearchQuality;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Records search-quality signals (search, follow-through, grade) into the search_quality table.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md.
/// </summary>
public sealed class SqliteSearchQualityService(ISqliteConnectionFactory factory) : ISearchQualityService
{
    public async Task RecordSearchAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        string? sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default)
    {
        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);
        var topFilesJson = topSourceFiles.Count > 0 ? JsonSerializer.Serialize(topSourceFiles) : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await connection.ExecuteAsync(
            """
            INSERT INTO search_quality (correlation_id, query, scope, project_id, session_id,
                result_count, top_source_files, created_at)
            VALUES (@CorrelationId, @Query, @Scope, @ProjectId, @SessionId,
                @ResultCount, @TopSourceFiles, @CreatedAt)
            """,
            new
            {
                CorrelationId = correlationId,
                Query = query,
                Scope = scope,
                ProjectId = projectId,
                SessionId = sessionId,
                ResultCount = resultCount,
                TopSourceFiles = topFilesJson,
                CreatedAt = now
            }).ConfigureAwait(false);
    }

    public async Task RecordFollowThroughAsync(
        string correlationId,
        string filePath,
        CancellationToken ct = default)
    {
        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);

        // Read current follow_through_files, append, update count
        var existing = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT follow_through_files FROM search_quality WHERE correlation_id = @Id",
            new { Id = correlationId }).ConfigureAwait(false);

        var files = existing is not null ? JsonSerializer.Deserialize<List<string>>(existing) ?? [] : [];
        if (!files.Contains(filePath, StringComparer.Ordinal))
        {
            files.Add(filePath);
        }

        await connection.ExecuteAsync(
            """
            UPDATE search_quality
            SET follow_through_count = @Count, follow_through_files = @Files
            WHERE correlation_id = @Id
            """,
            new
            {
                Id = correlationId,
                files.Count,
                Files = JsonSerializer.Serialize(files)
            }).ConfigureAwait(false);
    }

    public async Task RecordGradeAsync(
        string projectId,
        string correlationId,
        int grade,
        string? note,
        CancellationToken ct = default)
    {
        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(
            """
            UPDATE search_quality
            SET usefulness_grade = @Grade, grade_note = @Note
            WHERE correlation_id = @Id
            """,
            new { Id = correlationId, Grade = grade, Note = note }).ConfigureAwait(false);
    }

    public async Task<SearchQualityMetrics> GetMetricsAsync(
        string? projectId,
        DateTimeOffset from,
        CancellationToken ct = default)
    {
        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);
        var fromUnix = from.ToUnixTimeSeconds();

        var row = await connection.QuerySingleOrDefaultAsync(
            """
            SELECT
                COUNT(*)                                    AS TotalSearches,
                SUM(CASE WHEN follow_through_count > 0 THEN 1 ELSE 0 END) AS FollowThroughSearches,
                SUM(CASE WHEN usefulness_grade IS NOT NULL THEN 1 ELSE 0 END) AS GradedSearches,
                AVG(CASE WHEN usefulness_grade IS NOT NULL THEN usefulness_grade * 1.0 END) AS AverageGrade
            FROM search_quality
            WHERE created_at >= @From
              AND (@ProjectId IS NULL OR project_id = @ProjectId)
            """,
            new { From = fromUnix, ProjectId = projectId }).ConfigureAwait(false);

        var total = (int)(row?.TotalSearches ?? 0);
        var followThrough = (int)(row?.FollowThroughSearches ?? 0);
        var graded = (int)(row?.GradedSearches ?? 0);
        var avgGrade = (double?)row?.AverageGrade ?? 0.0;

        var days = Math.Max(1, (int)(DateTimeOffset.UtcNow - from).TotalDays);

        return new SearchQualityMetrics(
            total,
            followThrough,
            graded,
            avgGrade,
            total > 0 ? (double)followThrough / total : 0.0,
            total > 0 ? (double)graded / total : 0.0,
            total / days);
    }

    public async Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoff = nowUnixSeconds - (long)retentionDays * 86_400;
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        // Retention is global, so no project filter: idx_sq_project_time leads with project_id and
        // serves ReadMetricsAsync, not this — SQLite skip-scans or scans here.
        return await connection.ExecuteAsync(
                new CommandDefinition("DELETE FROM search_quality WHERE created_at < @cutoff",
                    new { cutoff }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
