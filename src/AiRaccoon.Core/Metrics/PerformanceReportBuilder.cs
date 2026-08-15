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
    ///     Caps allocation by bounding the bucket count per series, not the window — the .feature
    ///     rules retention a best-effort limit, not a guarantee (docs/work/specs/PerformanceMetrics.feature,
    ///     "A bank holding more than four weeks is within contract, not over it"), so a wide window
    ///     must be honoured in full. An agent-supplied windowMinutes with no upper bound would
    ///     otherwise allocate ceil(window/bucket) buckets per series unbounded (e.g. ~18.9M bucket
    ///     objects across ~36 series at a 1-year window and the 1-minute default bucket); instead the
    ///     bucket widens to fit the window within this cap. At ~36 series and a ~64-byte
    ///     <see cref="PerformanceBucket" /> object, 2000 * 36 * 64B ~= 4.6MB worst case, against the
    ///     ~18.9M-object unbounded case — plainly safe, while still ten-plus times finer than the
    ///     180-bucket default, so no realistic caller notices the cap.
    /// </summary>
    public const int MaxBucketCount = 2000;

    /// <summary>
    ///     Builds one series per <paramref name="toolNames" /> entry — the derived tool inventory, not
    ///     whatever names happen to appear in <paramref name="samples" /> — plus, when given, one more
    ///     per <paramref name="phaseNames" /> entry (WP10: the search-phase measurements, so they read
    ///     alongside the tool series rather than being dropped for having a name that is not a tool).
    ///     Both lists follow the same derived-inventory contract: a name with zero samples is still
    ///     present, at count zero. Bucketed across [<paramref name="now" /> - <paramref name="window" />,
    ///     <paramref name="now" />] — the window is never truncated. A bucket wider than the window
    ///     clamps to the window (one averaged point); a bucket that would push the per-series count
    ///     past <see cref="MaxBucketCount" /> widens instead, so the whole window still gets covered
    ///     within a bounded number of buckets.
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

        var start = now - window;
        var boundedBucket = bucket > window ? window : bucket;
        var minBucketForCap = TimeSpan.FromTicks((long)Math.Ceiling(window.Ticks / (double)MaxBucketCount));
        var effectiveBucket = boundedBucket < minBucketForCap ? minBucketForCap : boundedBucket;
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
        // The caller's SQL boundary floors `start` to a whole second (the metrics table only stores
        // whole-second recorded_at), so a sub-second `now` can hand this a row from just before the
        // true window start. Filtering here — not trusting the caller's boundary — keeps
        // sum(buckets[].Count) equal to Count always, not merely when now lands on a whole second.
        var toolSamples = samples.Where(s => s.Name == tool && s.RecordedAt >= start && s.RecordedAt <= now).ToList();
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
