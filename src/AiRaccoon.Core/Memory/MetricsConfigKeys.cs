namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the metrics writer and reaper (buffer cap, flush interval, hot-table retention).</summary>
public static class MetricsConfigKeys
{
    public const string BufferCapacityGlobal = "metrics.buffer-capacity.global";

    public const int DefaultBufferCapacity = 1000;

    public static int ParseBufferCapacity(string? value) =>
        int.TryParse(value, out var capacity) && capacity > 0 ? capacity : DefaultBufferCapacity;

    public const string FlushIntervalSecondsGlobal = "metrics.flush-interval-seconds.global";

    public const int DefaultFlushIntervalSeconds = 30;

    public static int ParseFlushIntervalSeconds(string? value) =>
        int.TryParse(value, out var seconds) && seconds > 0 ? seconds : DefaultFlushIntervalSeconds;

    /// <summary>Best-effort retention for the hot `metrics` table (docs/plans/2026-08-15-performance-metrics-implementation.md, WP4).</summary>
    public const string RetentionDaysGlobal = "metrics.retention-days.global";

    public const int DefaultRetentionDays = 28;

    public static int ParseRetentionDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultRetentionDays;
}
