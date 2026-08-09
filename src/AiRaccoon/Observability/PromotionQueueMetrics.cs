using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Observability;

/// <summary>
///     OpenTelemetry-compatible metrics for the propose tier. Meter name "AiRaccoon.PromotionQueue"
///     for dotnet-counters; queue lifecycle events (queued/evicted/promoted/discarded) are
///     distinct from tool invocations, so this is a separate class from ToolCallMetrics.
/// </summary>
public sealed class PromotionQueueMetrics : IPromotionQueueMetrics, IDisposable
{
    private readonly Counter<long> _evictions;
    private readonly Histogram<double> _evictedScore;
    private readonly Histogram<double> _waitSeconds;
    private readonly Counter<long> _promoted;
    private readonly Counter<long> _discarded;
    private QueueSnapshot? _snapshot;

    public PromotionQueueMetrics()
    {
        Meter = new Meter(OtlpNames.PromotionQueueScope);
        _evictions = Meter.CreateCounter<long>(
            OtlpNames.QueueEvictions,
            unit: "{eviction}",
            description: "Propose-tier evictions (reason tag: capacity)");
        _evictedScore = Meter.CreateHistogram<double>(
            OtlpNames.QueueEvictedScore,
            unit: "1",
            description: "Score of the evicted row");
        _waitSeconds = Meter.CreateHistogram<double>(
            OtlpNames.QueueWait,
            unit: "s",
            description: "Seconds a row waited before leaving the queue (promoted or discarded)");
        _promoted = Meter.CreateCounter<long>(
            OtlpNames.QueuePromoted,
            unit: "{row}",
            description: "Rows promoted from the queue into the shared tier");
        _discarded = Meter.CreateCounter<long>(
            OtlpNames.QueueDiscarded,
            unit: "{row}",
            description: "Rows discarded from the queue by the agent");
        Meter.CreateObservableUpDownCounter(
            OtlpNames.QueueQueued,
            ObserveQueued,
            unit: "{item}",
            description: "Queued promotions per project, read from the store's current state. "
                + "No measurement is published until the first propose/promote/discard/RecordSnapshot call in this process. "
                + "A project's series stops reporting rather than reporting 0 once it drains to empty — it retires, "
                + "it is not a confident 0.");
        Meter.CreateObservableGauge(
            OtlpNames.QueueCapacityUtilization,
            ObserveUtilization,
            unit: "1",
            description: "Queue occupancy against the cap (1.0 = full). "
                + "No measurement is published until the first propose/promote/discard/RecordSnapshot call in this process — "
                + "a gap, not a confident 0.0, until then.");
    }

    /// <summary>Meter named "AiRaccoon.PromotionQueue" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    public void RecordEviction(string projectId, double victimScore, string reason)
    {
        var tags = new TagList { { "project_id", projectId }, { "reason", reason } };
        _evictions.Add(1, tags);
        _evictedScore.Record(victimScore, tags);
    }

    // outcome (promoted|discarded) costs two series, so the cardinality reasoning that omits
    // project_id here (ADR-0009) does not apply (docs/work/2026-08-09-otlp-fix-plan.md WP10).
    public void RecordPromoted(string projectId, double waitSeconds)
    {
        _promoted.Add(1, new TagList { { "project_id", projectId } });
        _waitSeconds.Record(waitSeconds, new TagList { { "outcome", "promoted" } });
    }

    public void RecordDiscarded(string projectId, double waitSeconds)
    {
        _discarded.Add(1, new TagList { { "project_id", projectId } });
        _waitSeconds.Record(waitSeconds, new TagList { { "outcome", "discarded" } });
    }

    public void RecordSnapshot(PromotionQueueStats stats, int capacity) =>
        Volatile.Write(ref _snapshot, QueueSnapshot.From(stats, capacity));

    private IEnumerable<Measurement<long>> ObserveQueued()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is null
            ? []
            : snapshot.PerProject.Select(p => new Measurement<long>(p.Value, new TagList { { "project_id", p.Key } }));
    }

    private IEnumerable<Measurement<double>> ObserveUtilization()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is null ? [] : [new Measurement<double>(snapshot.Utilization)];
    }

    public void Dispose() => Meter.Dispose();

    /// <summary>Immutable, so a single Volatile field swap is enough to publish it (ADR-0009's
    /// zero-background-threads guarantee — no timer, the writers publish on the request path).
    /// A <see langword="null" /> <see cref="_snapshot" /> means "never published" and must yield
    /// no measurement, not a measured zero.</summary>
    private sealed record QueueSnapshot(IReadOnlyDictionary<string, int> PerProject, double Utilization)
    {
        public static QueueSnapshot From(PromotionQueueStats stats, int capacity) =>
            new(stats.PerProject, stats.TotalCount / (double)Math.Max(1, capacity));
    }
}
