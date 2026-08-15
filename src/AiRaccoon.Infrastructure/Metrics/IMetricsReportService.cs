namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>Produces the `memory_performance` report for one project (WP6, project-scoped only per ruling 1).</summary>
public interface IMetricsReportService
{
    /// <summary>
    ///     Window defaults to 3 hours, bucket to 1 minute, when either is omitted
    ///     (<see cref="PerformanceReportBuilder.DefaultWindow" />/<see cref="PerformanceReportBuilder.DefaultBucket" />).
    /// </summary>
    Task<PerformanceReport> GetReportAsync(
        string projectId,
        IReadOnlyList<string> toolNames,
        TimeSpan? window,
        TimeSpan? bucket,
        CancellationToken cancellationToken = default);
}
