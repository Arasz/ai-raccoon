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

    /// <summary>
    ///     Prefix for maintenance-job series (WP3, #477): <c>job.&lt;name&gt;.duration_ms</c> on every
    ///     completed run, <c>job.&lt;name&gt;.rows</c> where the runner can read an outstanding-row
    ///     count. Job names live in <c>AiRaccoon.Infrastructure.Maintenance</c>, which this project
    ///     cannot reference, so callers derive series names from a job name rather than this class
    ///     holding a hand-maintained list of them.
    /// </summary>
    public const string JobMetricPrefix = "job.";

    /// <summary>The <c>job.&lt;name&gt;.duration_ms</c> series name for one maintenance job.</summary>
    public static string JobDurationMetricName(string jobName) => $"{JobMetricPrefix}{jobName}.duration_ms";

    /// <summary>The <c>job.&lt;name&gt;.rows</c> series name for one maintenance job.</summary>
    public static string JobRowsMetricName(string jobName) => $"{JobMetricPrefix}{jobName}.rows";

    /// <summary>Prefix for embed-drain pass series (WP11): <c>drain.&lt;corpus&gt;.rows</c> and <c>drain.&lt;corpus&gt;.duration_ms</c>, bank-wide self-metrics like job.*.</summary>
    public const string DrainMetricPrefix = "drain.";

    /// <summary>Prefix for write-path series (WP11/WP12): <c>write.replace.wait_ms</c>,
    /// <c>write.replace.held_ms</c> and <c>write.replace.rows</c>.</summary>
    public const string WriteMetricPrefix = "write.";

    /// <summary>Prefix for query-time series (WP11): today just <c>search.query.truncated_tokens</c>.</summary>
    public const string SearchQueryMetricPrefix = "search.query.";

    /// <summary>
    ///     Every internal-series prefix MetricsReportService's discovery and the internal recorders'
    ///     own naming both consume — one constant set, so a fifth family cannot be added on one side
    ///     only (derive-or-delete).
    /// </summary>
    public static readonly IReadOnlyList<string> InternalSeriesPrefixes =
        [JobMetricPrefix, DrainMetricPrefix, WriteMetricPrefix, SearchQueryMetricPrefix];

    /// <summary>The <c>drain.&lt;corpus&gt;.rows</c> series name for one embed-drain pass.</summary>
    public static string DrainRowsMetricName(string corpus) => $"{DrainMetricPrefix}{corpus}.rows";

    /// <summary>The <c>drain.&lt;corpus&gt;.duration_ms</c> series name for one embed-drain pass.</summary>
    public static string DrainDurationMetricName(string corpus) => $"{DrainMetricPrefix}{corpus}.duration_ms";

    /// <summary>Replace-by-path's time spent waiting for the write lock, per transaction (WP12; EventId 899 recorded as a measurement).</summary>
    public const string ReplaceWaitMsMetricName = $"{WriteMetricPrefix}replace.wait_ms";

    /// <summary>Replace-by-path's write-lock hold time, per transaction — the chunker never runs inside it (WP12; EventId 899 recorded as a measurement).</summary>
    public const string ReplaceHeldMsMetricName = $"{WriteMetricPrefix}replace.held_ms";

    /// <summary>Rows a replace-by-path transaction wrote, per transaction.</summary>
    public const string ReplaceRowsMetricName = $"{WriteMetricPrefix}replace.rows";

    /// <summary>Tokens a search query exceeded the embedding window by, per truncated query (EventId 426 recorded as a measurement).</summary>
    public const string QueryTruncatedTokensMetricName = $"{SearchQueryMetricPrefix}truncated_tokens";
}
