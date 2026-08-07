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
    private readonly UpDownCounter<long> _queued;
    private readonly Counter<long> _evictions;
    private readonly Histogram<double> _evictedScore;
    private readonly Histogram<double> _waitSeconds;
    private readonly Counter<long> _promoted;
    private readonly Counter<long> _discarded;
    private double _utilization;

    public PromotionQueueMetrics()
    {
        Meter = new Meter("AiRaccoon.PromotionQueue");
        _queued = Meter.CreateUpDownCounter<long>(
            "ai_raccoon_queue_queued",
            description: "Queued promotions per project (delta)");
        _evictions = Meter.CreateCounter<long>(
            "ai_raccoon_queue_evictions_total",
            description: "Propose-tier evictions (reason tag: capacity)");
        _evictedScore = Meter.CreateHistogram<double>(
            "ai_raccoon_queue_evicted_score",
            description: "Score of the evicted row");
        _waitSeconds = Meter.CreateHistogram<double>(
            "ai_raccoon_queue_wait_seconds",
            description: "Seconds a row waited before leaving the queue (promoted or discarded)");
        _promoted = Meter.CreateCounter<long>(
            "ai_raccoon_queue_promoted_total",
            description: "Rows promoted from the queue into the shared tier");
        _discarded = Meter.CreateCounter<long>(
            "ai_raccoon_queue_discarded_total",
            description: "Rows discarded from the queue by the agent");
        Meter.CreateObservableGauge(
            "ai_raccoon_queue_capacity_utilization",
            () => Volatile.Read(ref _utilization),
            description: "Queue occupancy against the cap (1.0 = full)");
    }

    /// <summary>Meter named "AiRaccoon.PromotionQueue" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    public void RecordQueued(string projectId, int delta) =>
        _queued.Add(delta, new TagList { { "project_id", projectId } });

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

    public void RecordUtilization(double ratio) => Volatile.Write(ref _utilization, ratio);

    public void Dispose() => Meter.Dispose();
}
