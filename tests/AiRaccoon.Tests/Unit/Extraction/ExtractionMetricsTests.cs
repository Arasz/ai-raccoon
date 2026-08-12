using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     Extraction detail moved from logs to metrics-only: a real extraction pass, wired through the same
///     <see cref="PromotionQueueMetrics" /> port the propose tier already uses (no parallel metrics class),
///     increments the queued/promoted counters tagged by project_id.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractionMetricsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static ExtractionCandidateRow Row(string hash, string value = "organic fact about beta") => new(hash, $"{hash}.md", value, null, 0.5, 0, FixedNow.AddDays(-5), null);

    private static (ExtractionHostedService Service, PromotionQueueService Queue) NewStack(
        FakeExtractionStore store, InMemoryPromotionQueueStore queueStore, PromotionQueueMetrics metrics,
        FakeTimeProvider time)
    {
        var queue = new PromotionQueueService(queueStore, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, time);
        var runner = new SharedExtractionRunner(store, new SharedExtractionService(), queue, time);
        var service = new ExtractionHostedService(store, runner, queue, time, TestTelemetry.None,
            NullLogger<ExtractionHostedService>.Instance);
        return (service, queue);
    }

    [Fact]
    public async Task RunOnce_ProposeMode_IncrementsTheQueuedCounter_TaggedByProject()
    {
        var store = new FakeExtractionStore();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] = [Row("h1")];
        store.Candidates["beta"] = [];
        var time = new FakeTimeProvider(FixedNow);
        using var metrics = new PromotionQueueMetrics();
        var (service, _) = NewStack(store, new InMemoryPromotionQueueStore(), metrics, time);
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueQueued);

        await service.RunOnceAsync(TestContext.Current.CancellationToken);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public async Task RunOnce_PromoteMode_IncrementsThePromotedCounter_TaggedByProject()
    {
        var store = new FakeExtractionStore();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1")];
        store.Candidates["beta"] = [];
        var time = new FakeTimeProvider(FixedNow);
        using var metrics = new PromotionQueueMetrics();
        var queueStore = new InMemoryPromotionQueueStore();
        var (service, queue) = NewStack(store, queueStore, metrics, time);
        // Promote drains from the queue, not from a fresh extraction — seed it via a propose pass.
        await queue.ProposeAsync("acme",
            [new QueueCandidate("h1", "h1.md", "organic fact about beta", null, 3.0, ["organic-write"])],
            TestContext.Current.CancellationToken);
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueuePromoted);

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    /// <summary>Minimal working <see cref="IPromotionQueueStore" /> fake — the guard/race test
    /// fakes elsewhere in this folder are deliberately partial (throwing or scripted); this one
    /// behaves like the real SQLite-backed store closely enough to exercise a full propose+promote
    /// round trip without a database.</summary>
    private sealed class InMemoryPromotionQueueStore : IPromotionQueueStore
    {
        private readonly HashSet<(string ProjectId, string Hash)> _discarded = [];
        private readonly List<PromotionQueueRow> _rows = [];

        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
            CancellationToken cancellationToken = default)
        {
            var added = 0;
            foreach (var candidate in rows)
            {
                var existing = _rows.FindIndex(r => r.ProjectId == projectId && r.Hash == candidate.Hash);
                var updated = new PromotionQueueRow(projectId, candidate.Hash, candidate.Path, candidate.Value,
                    candidate.SourceFile, candidate.Score, candidate.Reasons,
                    existing >= 0 ? _rows[existing].CreatedAt : 0, 0, candidate.ScorerVersion);
                if (existing >= 0)
                {
                    _rows[existing] = updated;
                }
                else
                {
                    _rows.Add(updated);
                    added++;
                }
            }

            return Task.FromResult(added);
        }

        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromotionQueueRow>>(
                [.. _rows.Where(r => projectId is null || r.ProjectId == projectId)]);

        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
            CancellationToken cancellationToken = default)
        {
            var removed = _rows.Where(r => r.ProjectId == projectId && (hash is null || r.Hash == hash)).ToList();
            _rows.RemoveAll(removed.Contains);
            return Task.FromResult<IReadOnlyList<PromotionQueueRow>>(removed);
        }

        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionQueueStats(_rows.Count, null,
                _rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Count())));

        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
            CancellationToken cancellationToken = default)
        {
            var scoped = _rows.Where(r => projectId is null || r.ProjectId == projectId).ToList();
            return Task.FromResult(new PromotionWaitStats(scoped.Count, null, null,
                _rows.Select(r => r.ProjectId).Distinct(StringComparer.Ordinal).Count()));
        }

        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
            CancellationToken cancellationToken = default)
        {
            var victim = _rows.Where(r => r.ProjectId == projectId)
                .OrderBy(r => r.Score).ThenBy(r => r.CreatedAt).FirstOrDefault();
            if (victim is not null)
            {
                _rows.Remove(victim);
            }

            return Task.FromResult(victim);
        }

        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
            CancellationToken cancellationToken = default)
        {
            var removed = _rows.RemoveAll(r => r.ProjectId == projectId && r.ScorerVersion != currentScorerVersion);
            return Task.FromResult(removed);
        }

        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes,
            CancellationToken cancellationToken = default)
        {
            foreach (var hash in hashes)
            {
                _discarded.Add((projectId, hash));
            }

            return Task.CompletedTask;
        }

        public Task<int> PruneRejectedAsync(string projectId,
            CancellationToken cancellationToken = default)
        {
            var removed = _rows.RemoveAll(r => r.ProjectId == projectId && _discarded.Contains((r.ProjectId, r.Hash)));
            return Task.FromResult(removed);
        }
    }
}
