using System.Text.Json;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.SearchQuality;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Records search-quality signals (search, follow-through, grade) into the search_quality table.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md.
/// </summary>
public sealed partial class SqliteSearchQualityService(ISqliteConnectionFactory factory, ILogger<SqliteSearchQualityService> logger)
    : ISearchQualityService
{
    /// <summary>Compact Stage-1 feature JSON: camel-case keys, no indentation.</summary>
    private static readonly JsonSerializerOptions ResultFeaturesJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    /// <summary>Best-effort: never throws (docs/plans/2026-08-15-performance-metrics-implementation.md, WP1 tool-size step).</summary>
    public async Task RecordSearchSafeAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        IReadOnlyList<RetrievalEvidence>? evidence = null)
    {
        try
        {
            await RecordSearchAsync(correlationId, query, scope, projectId, null, resultCount, topSourceFiles, ct, evidence)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.RecordSearchSafeFailed(logger, ex, correlationId);
        }
    }

    public async Task RecordSearchAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        string? sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        // P6b: the P4 sidecar joined to the served rows in served order. Persisted as compact
        // JSON on the same single INSERT — never a per-result statement (M6/G5).
        IReadOnlyList<RetrievalEvidence>? evidence = null)
    {
        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);
        var topFilesJson = topSourceFiles.Count > 0 ? JsonSerializer.Serialize(topSourceFiles) : null;
        var resultFeaturesJson = SerializeResultFeatures(evidence);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await connection.ExecuteAsync(
            """
            INSERT INTO search_quality (correlation_id, query, scope, project_id, session_id,
                result_count, top_source_files, result_features, created_at)
            VALUES (@CorrelationId, @Query, @Scope, @ProjectId, @SessionId,
                @ResultCount, @TopSourceFiles, @ResultFeatures, @CreatedAt)
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
                ResultFeatures = resultFeaturesJson,
                CreatedAt = now
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stage-1 per-result features (plan §5, P6b): hashes, strengths, leg names/ranks,
    ///     cosines — never values or snippets. Null or empty evidence writes NULL (the
    ///     top_source_files precedent in the caller). Non-finite doubles become null: Core stays
    ///     JSON-free while System.Text.Json refuses to emit them, so the sanitize lives here (M2).
    /// </summary>
    private static string? SerializeResultFeatures(IReadOnlyList<RetrievalEvidence>? evidence)
    {
        if (evidence is null || evidence.Count == 0)
        {
            return null;
        }

        var rows = new List<ResultFeatureRow>(evidence.Count);
        foreach (var item in evidence)
        {
            var legs = new List<LegFeatureRow>(item.Legs.Count);
            foreach (var leg in item.Legs)
            {
                legs.Add(new LegFeatureRow(leg.LegName, leg.Rank));
            }

            rows.Add(new ResultFeatureRow(
                item.Hash,
                double.IsFinite(item.FusionStrength) ? item.FusionStrength : null,
                legs,
                item.Cosine is { } cosine && double.IsFinite(cosine) ? cosine : null));
        }

        return JsonSerializer.Serialize(rows, ResultFeaturesJson);
    }

    /// <summary>One served row's features: the <c>result_features</c> JSON element shape.</summary>
    private sealed record ResultFeatureRow(
        string Hash,
        double? Strength,
        IReadOnlyList<LegFeatureRow> Legs,
        double? Cosine);

    /// <summary>One leg's ordinal vote, mirroring <see cref="LegRank" />.</summary>
    private sealed record LegFeatureRow(string Name, int Rank);

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

    private static partial class Log
    {
        [LoggerMessage(EventId = 965, Level = LogLevel.Warning,
            Message = "Failed to record search quality for correlation {CorrelationId}")]
        public static partial void RecordSearchSafeFailed(ILogger logger, Exception exception, string correlationId);
    }
}
