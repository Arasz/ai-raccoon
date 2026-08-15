using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Metrics;

/// <summary>
///     Pure window/bucket aggregation over raw measurement samples — no I/O, no state (WP6,
///     docs/plans/2026-08-15-performance-metrics-implementation.md). Percentiles delegate to
///     <see cref="Statistics" /> (WP0, G2); this does not compute a second percentile.
/// </summary>
public static class PerformanceReportBuilder
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(3);
    public static readonly TimeSpan DefaultBucket = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     Builds one series per <paramref name="toolNames" /> entry — the derived tool inventory, not
    ///     whatever names happen to appear in <paramref name="samples" /> — plus, when given, one more
    ///     per <paramref name="phaseNames" /> entry (WP10: the search-phase measurements, so they read
    ///     alongside the tool series rather than being dropped for having a name that is not a tool).
    ///     Both lists follow the same derived-inventory contract: a name with zero samples is still
    ///     present, at count zero. Bucketed across [<paramref name="now" /> - <paramref name="window" />,
    ///     <paramref name="now" />]. A bucket wider than the window clamps to the window (one averaged point).
    /// </summary>
    public static PerformanceReport Build(
        IReadOnlyList<string> toolNames,
        IReadOnlyList<MetricSample> samples,
        DateTimeOffset now,
        TimeSpan window,
        TimeSpan bucket,
        IReadOnlyList<string>? phaseNames = null)
    {
        Guard.IsNotNull(toolNames);
        Guard.IsNotNull(samples);

        var effectiveBucket = bucket > window ? window : bucket;
        var start = now - window;
        var bucketCount = Math.Max(1, (int)Math.Ceiling(window / effectiveBucket));

        IReadOnlyList<string> seriesNames = phaseNames is { Count: > 0 } ? [.. toolNames, .. phaseNames] : toolNames;
        var series = seriesNames
            .Select(name => BuildSeries(name, samples, start, now, effectiveBucket, bucketCount))
            .ToList();

        return new PerformanceReport(now, window, effectiveBucket, bucketCount, series);
    }

    private static ToolPerformanceSeries BuildSeries(
        string tool, IReadOnlyList<MetricSample> samples, DateTimeOffset start, DateTimeOffset now,
        TimeSpan bucket, int bucketCount)
    {
        var toolSamples = samples.Where(s => s.Name == tool).ToList();
        var buckets = new List<PerformanceBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = start + bucket * i;
            var bucketEnd = i == bucketCount - 1 ? now : bucketStart + bucket;
            var inBucket = toolSamples.Where(s => s.RecordedAt >= bucketStart && s.RecordedAt < bucketEnd).ToList();
            var average = inBucket.Count > 0 ? Statistics.Mean([.. inBucket.Select(s => s.Value)]) : (double?)null;
            buckets.Add(new PerformanceBucket(bucketStart, inBucket.Count, average));
        }

        var values = toolSamples.Select(s => s.Value).ToList();
        if (values.Count == 0)
        {
            return new ToolPerformanceSeries(tool, 0, null, null, null, null, null, buckets);
        }

        return new ToolPerformanceSeries(
            tool,
            values.Count,
            Statistics.Percentile(values, 0.50),
            Statistics.Percentile(values, 0.95),
            Statistics.Percentile(values, 0.99),
            Statistics.Min(values),
            Statistics.Max(values),
            buckets);
    }
}
