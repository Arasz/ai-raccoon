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
    private QueueSnapshot _snapshot = QueueSnapshot.Empty;

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
            description: "Queued promotions per project, read from the store's current state");
        Meter.CreateObservableGauge(
            OtlpNames.QueueCapacityUtilization,
            ObserveUtilization,
            unit: "1",
            description: "Queue occupancy against the cap (1.0 = full)");
    }

    /// <summary>Meter named "AiRaccoon.PromotionQueue" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    public void RecordEviction(string projectId, double victimScore, string reason)
    {
        _evictions.Add(1, new TagList { { "project_id", projectId }, { "reason", reason } });
        _evictedScore.Record(victimScore);
    }

    public void RecordPromoted(string projectId, double waitSeconds)
    {
        _promoted.Add(1, new TagList { { "project_id", projectId } });
        _waitSeconds.Record(waitSeconds);
    }

    public void RecordDiscarded(string projectId, double waitSeconds)
    {
        _discarded.Add(1, new TagList { { "project_id", projectId } });
        _waitSeconds.Record(waitSeconds);
    }

    public void RecordSnapshot(PromotionQueueStats stats, int capacity) =>
        Volatile.Write(ref _snapshot, QueueSnapshot.From(stats, capacity));

    private IEnumerable<Measurement<long>> ObserveQueued() =>
        Volatile.Read(ref _snapshot).PerProject
            .Select(p => new Measurement<long>(p.Value, new TagList { { "project_id", p.Key } }));

    private double ObserveUtilization() => Volatile.Read(ref _snapshot).Utilization;

    public void Dispose() => Meter.Dispose();

    /// <summary>Immutable, so a single Volatile field swap is enough to publish it (ADR-0009's
    /// zero-background-threads guarantee — no timer, the writers publish on the request path).</summary>
    private sealed record QueueSnapshot(IReadOnlyDictionary<string, int> PerProject, double Utilization)
    {
        public static readonly QueueSnapshot Empty = new(new Dictionary<string, int>(), 0.0);

        public static QueueSnapshot From(PromotionQueueStats stats, int capacity) =>
            new(stats.PerProject, stats.TotalCount / (double)Math.Max(1, capacity));
    }
}
