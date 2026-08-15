namespace AiRaccoon.Core.Metrics;

/// <summary>One raw `metrics` row, projected to what the report builder needs (no I/O here).</summary>
public readonly record struct MetricSample(string Name, double Value, DateTimeOffset RecordedAt);

/// <summary>One bucket of a tool's series: the point count and their average, or an empty bucket.</summary>
public sealed record PerformanceBucket(DateTimeOffset Start, int Count, double? Average);

/// <summary>
///     One tool's window-wide summary (via <see cref="Core.Metrics.Statistics" />) plus its bucketed
///     points. A tool with no samples in the window is still present, at count zero
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP6 derived-inventory gate).
/// </summary>
public sealed record ToolPerformanceSeries(
    string Tool,
    int Count,
    double? P50,
    double? P95,
    double? P99,
    double? Min,
    double? Max,
    IReadOnlyList<PerformanceBucket> Buckets);

/// <summary>The `memory_performance` report: one series per tool on the derived inventory.</summary>
public sealed record PerformanceReport(
    DateTimeOffset GeneratedAt,
    TimeSpan Window,
    TimeSpan Bucket,
    int BucketCount,
    IReadOnlyList<ToolPerformanceSeries> Series);
