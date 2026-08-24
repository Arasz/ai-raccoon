using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Reads the bank's `metrics` table (WP0 schema, WP3's writer) for one project and hands the raw
///     samples to <see cref="PerformanceReportBuilder" /> — this class owns the I/O, the builder owns
///     the aggregation (WP6, docs/plans/2026-08-15-performance-metrics-implementation.md). Also folds
///     in <see cref="SearchTimings.SeriesNames" /> (WP10, docs/adr/0079) so the search-phase and
///     -total measurements the tool layer records reach the report the same way the tool series do —
///     the SQL filter has to know about them too, not just the builder, or they never leave the table.
/// </summary>
public sealed class MetricsReportService(ISqliteConnectionFactory factory, TimeProvider timeProvider) : IMetricsReportService
{
    private const string SelectSql = """
                                     SELECT name, value, recorded_at
                                     FROM metrics
                                     WHERE project_id = @ProjectId AND name IN @SeriesNames
                                       AND recorded_at >= @FromUnix AND recorded_at <= @ToUnix
                                     """;

    /// <summary>
    ///     WP3 (#477) / WP11 (log-values-as-metrics): every internal-series family — job.&lt;name&gt;.*,
    ///     drain.&lt;corpus&gt;.*, write.replace.*, search.query.* — is either dynamic (defined in a
    ///     layer this project cannot reference) or bank-wide, so it is discovered by prefix from what
    ///     the window actually holds for THIS report's project id, never a hand-maintained name list
    ///     (derive-or-delete). Scoped to @ProjectId, not just the self-metrics id: write.replace.* is
    ///     recorded under the writing project's own id, so an ordinary project's report must discover
    ///     it too.
    /// </summary>
    private static readonly string DiscoverInternalSeriesNamesSql = $"""
                                                                     SELECT DISTINCT name
                                                                     FROM metrics
                                                                     WHERE project_id = @ProjectId AND ({string.Join(" OR ",
                                                                         MetricsConfigKeys.InternalSeriesPrefixes.Select((_, i) => $"name LIKE @Prefix{i}"))})
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
        // Self-metrics are bank-wide, not project-scoped, so they ride in only when the caller asks
        // for the self-metrics report specifically — never folded into an ordinary project's series,
        // which would misattribute a bank-wide number to whichever project happened to ask first.
        var isSelfMetricsReport = string.Equals(projectId, MetricsConfigKeys.SelfMetricsProjectId, StringComparison.Ordinal);
        var selfMetricNames = isSelfMetricsReport ? MetricsConfigKeys.SelfMetricNames : [];
        var from = now - effectiveWindow;

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var internalMetricNames = await DiscoverInternalMetricNamesAsync(connection, projectId, from, now, cancellationToken)
            .ConfigureAwait(false);

        // toolNames + phaseNames can never be empty: SearchTimings.SeriesNames always holds at
        // least the phases plus the total, so this always goes through the query below — no
        // separate empty-list branch.
        var phaseNames = (IReadOnlyList<string>)[.. SearchTimings.SeriesNames, .. selfMetricNames, .. internalMetricNames];
        var seriesNames = (IReadOnlyList<string>)[.. toolNames, .. phaseNames];

        var rows = await connection.QueryAsync<MetricRow>(new CommandDefinition(SelectSql, new
        {
            ProjectId = projectId,
            SeriesNames = seriesNames,
            FromUnix = from.ToUnixTimeSeconds(),
            ToUnix = now.ToUnixTimeSeconds()
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var samples = rows
            .Select(r => new MetricSample(r.Name, r.Value, DateTimeOffset.FromUnixTimeSeconds(r.RecordedAt)))
            .ToList();

        return PerformanceReportBuilder.Build(toolNames, samples, now, effectiveWindow, effectiveBucket, phaseNames);
    }

    private static async Task<IReadOnlyList<string>> DiscoverInternalMetricNamesAsync(SqliteConnection connection,
        string projectId, DateTimeOffset from, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", projectId);
        parameters.Add("FromUnix", from.ToUnixTimeSeconds());
        parameters.Add("ToUnix", now.ToUnixTimeSeconds());
        for (var i = 0; i < MetricsConfigKeys.InternalSeriesPrefixes.Count; i++)
        {
            parameters.Add($"Prefix{i}", MetricsConfigKeys.InternalSeriesPrefixes[i] + "%");
        }

        var names = await connection.QueryAsync<string>(new CommandDefinition(DiscoverInternalSeriesNamesSql,
            parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. names];
    }

    private sealed record MetricRow(string Name, double Value, long RecordedAt);
}
