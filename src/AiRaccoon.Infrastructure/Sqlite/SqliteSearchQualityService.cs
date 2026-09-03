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
    /// <summary>Best-effort: never throws (docs/plans/2026-08-15-performance-metrics-implementation.md, WP1 tool-size step).</summary>
    public async Task RecordSearchSafeAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        string kind,
        string sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        IReadOnlyList<RetrievalEvidence>? evidence = null)
    {
        try
        {
            await RecordSearchAsync(correlationId, query, scope, projectId, kind, sessionId, resultCount, topSourceFiles, ct, evidence)
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
        string kind,
        string sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        // P6a threading only: accepted so the dispatcher can pass the sidecar through.
        // P6b persists it; until then it is ignored and the row writes exactly as before.
        IReadOnlyList<RetrievalEvidence>? evidence = null)
    {
        if (kind is not ("memory" or "code" or "both"))
        {
            throw new ArgumentException($"Invalid kind '{kind}': expected memory, code, or both.", nameof(kind));
        }

        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);
        var topFilesJson = topSourceFiles.Count > 0 ? JsonSerializer.Serialize(topSourceFiles) : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await connection.ExecuteAsync(
            """
            INSERT INTO search_quality (correlation_id, query, scope, project_id, session_id, kind,
                result_count, top_source_files, created_at)
            VALUES (@CorrelationId, @Query, @Scope, @ProjectId, @SessionId, @Kind,
                @ResultCount, @TopSourceFiles, @CreatedAt)
            """,
            new
            {
                CorrelationId = correlationId,
                Query = query,
                Scope = scope,
                ProjectId = projectId,
                SessionId = sessionId,
                Kind = kind,
                ResultCount = resultCount,
                TopSourceFiles = topFilesJson,
                CreatedAt = now
            }).ConfigureAwait(false);
    }

    public async Task RecordFollowThroughAsync(
        string correlationId,
        string filePath,
        int? servedRank = null,
        CancellationToken ct = default)
    {
        if (servedRank.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(servedRank.Value, 1);
        }

        await using var connection = await factory.OpenBankAsync(ct).ConfigureAwait(false);

        // The codec is the migration (no DDL): legacy plain-string cells upgrade in memory to
        // uniform object rows on append, and the cell always round-trips the object shape.
        // Dedupe is ordinal by path: an existing non-null rank is never clobbered, and a null
        // rank is filled once by a later non-null.
        var existing = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT follow_through_files FROM search_quality WHERE correlation_id = @Id",
            new { Id = correlationId }).ConfigureAwait(false);

        var entries = DecodeFollowThrough(existing);
        var index = entries.FindIndex(e => string.Equals(e.Path, filePath, StringComparison.Ordinal));
        if (index < 0)
        {
            entries.Add(new FollowThroughEntry(filePath, servedRank));
        }
        else if (entries[index].Rank is null && servedRank.HasValue)
        {
            entries[index] = entries[index] with { Rank = servedRank };
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
                entries.Count,
                Files = JsonSerializer.Serialize(entries)
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Decodes a <c>follow_through_files</c> cell: uniform object rows, with a per-element
    ///     fallback for legacy plain-string rows (pre-P4 writers, including the file-watcher
    ///     hook, which still appends bare strings). Unknown element shapes are skipped.
    /// </summary>
    private static List<FollowThroughEntry> DecodeFollowThrough(string? cell)
    {
        if (string.IsNullOrEmpty(cell))
        {
            return [];
        }

        using var document = JsonDocument.Parse(cell);
        var entries = new List<FollowThroughEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                entries.Add(new FollowThroughEntry(element.GetString() ?? string.Empty, null));
            }
            else if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("path", out var pathProperty)
                && pathProperty.GetString() is { } path)
            {
                int? rank = null;
                if (element.TryGetProperty("rank", out var rankProperty)
                    && rankProperty.ValueKind == JsonValueKind.Number
                    && rankProperty.TryGetInt32(out var parsed))
                {
                    rank = parsed;
                }

                entries.Add(new FollowThroughEntry(path, rank));
            }
        }

        return entries;
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
