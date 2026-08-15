using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Reads the bank's `metrics` table (WP0 schema, WP3's writer) for one project and hands the raw
///     samples to <see cref="PerformanceReportBuilder" /> — this class owns the I/O, the builder owns
///     the aggregation (WP6, docs/plans/2026-08-15-performance-metrics-implementation.md).
/// </summary>
public sealed class MetricsReportService(ISqliteConnectionFactory factory, TimeProvider timeProvider) : IMetricsReportService
{
    private const string SelectSql = """
                                      SELECT name, value, recorded_at
                                      FROM metrics
                                      WHERE project_id = @ProjectId AND name IN @ToolNames
                                        AND recorded_at >= @FromUnix AND recorded_at <= @ToUnix
                                      """;

    public async Task<PerformanceReport> GetReportAsync(
        string projectId,
        IReadOnlyList<string> toolNames,
        TimeSpan? window,
        TimeSpan? bucket,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        Guard.IsNotNull(toolNames);

        var now = timeProvider.GetUtcNow();
        var effectiveWindow = window ?? PerformanceReportBuilder.DefaultWindow;
        var effectiveBucket = bucket ?? PerformanceReportBuilder.DefaultBucket;

        if (toolNames.Count == 0)
        {
            return PerformanceReportBuilder.Build(toolNames, [], now, effectiveWindow, effectiveBucket);
        }

        var from = now - effectiveWindow;

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MetricRow>(new CommandDefinition(SelectSql, new
        {
            ProjectId = projectId,
            ToolNames = toolNames,
            FromUnix = from.ToUnixTimeSeconds(),
            ToUnix = now.ToUnixTimeSeconds()
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var samples = rows
            .Select(r => new MetricSample(r.Name, r.Value, DateTimeOffset.FromUnixTimeSeconds(r.RecordedAt)))
            .ToList();

        return PerformanceReportBuilder.Build(toolNames, samples, now, effectiveWindow, effectiveBucket);
    }

    private sealed record MetricRow(string Name, double Value, long RecordedAt);
}
