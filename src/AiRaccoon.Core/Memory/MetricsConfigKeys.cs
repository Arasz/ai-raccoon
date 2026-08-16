namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the metrics writer and reaper (buffer cap, flush interval, hot-table retention).</summary>
public static class MetricsConfigKeys
{
    public const string BufferCapacityGlobal = "metrics.buffer-capacity.global";

    public const int DefaultBufferCapacity = 1000;

    /// <summary>Ceiling the CLI enforces on write: buffer capacity is otherwise an allocation an operator could set arbitrarily large.</summary>
    public const int MaxBufferCapacity = 1_000_000;

    public static int ParseBufferCapacity(string? value) =>
        int.TryParse(value, out var capacity) && capacity > 0 ? capacity : DefaultBufferCapacity;

    public const string FlushIntervalSecondsGlobal = "metrics.flush-interval-seconds.global";

    public const int DefaultFlushIntervalSeconds = 30;

    public static int ParseFlushIntervalSeconds(string? value) =>
        int.TryParse(value, out var seconds) && seconds > 0 ? seconds : DefaultFlushIntervalSeconds;

    /// <summary>Best-effort retention for the hot `metrics` table (docs/plans/2026-08-15-performance-metrics-implementation.md, WP4).</summary>
    public const string RetentionDaysGlobal = "metrics.retention-days.global";

    public const int DefaultRetentionDays = 28;

    /// <summary>Ceiling the CLI enforces on write: MetricsRetentionJob computes DateTimeOffset.AddDays(-days), which throws beyond a bound far smaller than int.MaxValue.</summary>
    public const int MaxRetentionDays = 36500;

    public static int ParseRetentionDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultRetentionDays;

    /// <summary>
    ///     The sentinel project id self-metrics (flush duration/batch size, drop count) are tagged
    ///     with — they are bank-wide, not scoped to any one project, so no real project id fits.
    ///     Not a real project: reachable only by asking for a report on this id specifically, never
    ///     leaking into an ordinary project's report (review-fixes finding 5).
    /// </summary>
    public const string SelfMetricsProjectId = "__self_metrics__";

    /// <summary>The measurement names MetricsFlusher writes directly to the store on every flush pass.</summary>
    public static readonly IReadOnlyList<string> SelfMetricNames =
        ["metrics.flush.duration_ms", "metrics.flush.batch_size", "metrics.dropped"];
}
