using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     PromoteAsync snapshots the queue, then drains and shares each row; a row gone by drain
///     time — raced by a concurrent discard — must never be shared. Fakes make the race
///     deterministic instead of depending on real thread timing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionQueueServicePromoteRaceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PromoteAsync_NeverShares_ARowAnotherCallerAlreadyClaimed()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));
        queue.Rows.Add(new PromotionQueueRow("acme", "h2", "h2.md", "fact two", null, 3.0, [], 0, 0));
        // Simulates memory_promotion_discard("acme", "h1") winning the race between this
        // PromoteAsync's ListAsync snapshot and the moment it reaches h1's turn to drain.
        queue.AlreadyGoneHashes.Add("h1");

        var store = new RecordingShareStore();
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken);

        store.SharedHashes.ShouldNotContain("h1",
            "h1 was already claimed by a concurrent discard — sharing it would resurrect a rejected " +
            "candidate into the sweep-exempt shared tier with no way to undo it");
        outcome.PromotedHashes.ShouldNotContain("h1");
        store.SharedHashes.ShouldBe(["h2"]);
        outcome.PromotedHashes.ShouldBe(["h2"]);
        metrics.Snapshots.ShouldHaveSingleItem().Stats.TotalCount.ShouldBe(0,
            "h1 was claimed by a concurrent discard and h2 was drained by this call — the store is empty; " +
            "RecordSnapshot reports its real state, not this call's own delta");
    }

    [Fact]
    public async Task PromoteAsync_StaleQueuedHash_IsDropped_TheRestPromote_AndTheOutcomeSaysSo()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));
        queue.Rows.Add(new PromotionQueueRow("acme", "h2", "h2.md", "fact two", null, 3.0, [], 0, 0));

        var store = new RecordingShareStore();
        // h1 was claimed (discard succeeded) but the entry it points at is gone by share time —
        // the scenario WP1's trigger normally prevents, and this test is the safety net for it.
        store.FailingHashes["h1"] = new UnknownHashException("h1", "acme");
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe(["h2"],
            "h1's failure must not stop h2, which was already claimed and ready to share");
        store.SharedHashes.ShouldBe(["h2"]);
        outcome.Failures.ShouldHaveSingleItem();
        outcome.Failures[0].ProjectId.ShouldBe("acme");
        outcome.Failures[0].Hash.ShouldBe("h1");
        outcome.Failures[0].Reason.ShouldBe("stale-hash");
        queue.Rows.ShouldBeEmpty("h1 was already claimed off the queue and must not be re-queued");
    }

    [Fact]
    public async Task PromoteAsync_AFailureInOneProject_DoesNotStopTheNext()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));
        queue.Rows.Add(new PromotionQueueRow("beta", "h2", "h2.md", "fact two", null, 5.0, [], 0, 0));

        var store = new RecordingShareStore();
        store.FailingHashes["h1"] = new InvalidOperationException("transient share failure");
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme", "beta"], 10, cancellationToken: TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe(["h2"], "acme's failure must not stop beta from promoting");
        outcome.Failures.ShouldHaveSingleItem();
        outcome.Failures[0].ProjectId.ShouldBe("acme");
        outcome.Failures[0].Hash.ShouldBe("h1");
        outcome.Failures[0].Reason.ShouldBe("share-failed");
    }

    /// <summary>
    ///     A-F11 (WP5b): PromoteAsync used to claim a row by DELETE, so a transient ShareAsync
    ///     failure — a locked database, a disk-full write — destroyed the candidate permanently.
    ///     The RED case named by the acceptance criteria: the row must survive a failure other than
    ///     UnknownHashException, reclaimable rather than gone.
    /// </summary>
    [Fact]
    public async Task PromoteAsync_TransientShareFailure_LeavesTheRowClaimed_NotDestroyed()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));

        var store = new RecordingShareStore();
        store.FailingHashes["h1"] = new InvalidOperationException("disk full mid-share");
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken);

        outcome.Failures.ShouldHaveSingleItem().Reason.ShouldBe("share-failed");
        queue.Rows.ShouldContain(r => r.Hash == "h1",
            "a transient ShareAsync failure must leave the candidate reclaimable, not destroy it");
        queue.Claimed.ShouldContain(("acme", "h1"), "the row stays claimed until the stale-claim sweep releases it");
    }

    /// <summary>An UnknownHashException means the backing entry is genuinely gone — retrying can
    /// never succeed, so this claim is released permanently rather than left for the sweep.</summary>
    [Fact]
    public async Task PromoteAsync_UnknownHashException_RemovesTheRowPermanently()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));

        var store = new RecordingShareStore();
        store.FailingHashes["h1"] = new UnknownHashException("h1", "acme");
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        await service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken);

        queue.Rows.ShouldBeEmpty("a genuinely gone backing entry must not be left claimed forever");
    }

    /// <summary>PromoteAsync sweeps stale claims before draining, so a claim stuck since a prior
    /// crashed pass is retried rather than skipped forever.</summary>
    [Fact]
    public async Task PromoteAsync_ReclaimsStaleClaims_BeforeDraining_SoAnEarlierStuckClaimIsRetried()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));
        queue.Claimed.Add(("acme", "h1")); // simulates a stuck claim from a prior crashed pass

        var store = new RecordingShareStore();
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe(["h1"], "the stale claim must be released before this pass drains the queue");
    }

    [Fact]
    public async Task PromoteAsync_Cancellation_StillPropagates()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));

        var store = new RecordingShareStore();
        store.FailingHashes["h1"] = new OperationCanceledException();
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            service.PromoteAsync(["acme"], 10, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class RaceyQueueStore : IPromotionQueueStore
    {
        public Task<int> PurgeOldDiscardsAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<PromotionQueueRow> Rows { get; } = [];
        public HashSet<string> AlreadyGoneHashes { get; } = new(StringComparer.Ordinal);

        /// <summary>Simulates promotion_queue.claimed_at — (project, hash) pairs currently claimed.</summary>
        public HashSet<(string ProjectId, string Hash)> Claimed { get; } = [];

        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromotionQueueRow>>(
                [.. Rows.Where(r => projectId is null || r.ProjectId == projectId)]);

        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
            CancellationToken cancellationToken = default)
        {
            if (hash is not null && AlreadyGoneHashes.Contains(hash))
            {
                // The concurrent caller's claim already applied to the store — this call gets
                // nothing back, but the row is genuinely gone by the time GetStatsAsync runs.
                Rows.RemoveAll(r => r.ProjectId == projectId && r.Hash == hash);
                Claimed.Remove((projectId, hash));
                return Task.FromResult<IReadOnlyList<PromotionQueueRow>>([]);
            }

            var removed = Rows.Where(r => r.ProjectId == projectId && (hash == null || r.Hash == hash)).ToList();
            Rows.RemoveAll(removed.Contains);
            foreach (var row in removed)
            {
                Claimed.Remove((projectId, row.Hash));
            }

            return Task.FromResult<IReadOnlyList<PromotionQueueRow>>(removed);
        }

        public Task<PromotionQueueRow?> ClaimAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            if (AlreadyGoneHashes.Contains(hash))
            {
                Rows.RemoveAll(r => r.ProjectId == projectId && r.Hash == hash);
                return Task.FromResult<PromotionQueueRow?>(null);
            }

            if (!Claimed.Add((projectId, hash)))
            {
                return Task.FromResult<PromotionQueueRow?>(null);
            }

            var row = Rows.FirstOrDefault(r => r.ProjectId == projectId && r.Hash == hash);
            return Task.FromResult(row);
        }

        public Task<int> ReclaimStaleClaimsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
        {
            var released = Claimed.Count;
            Claimed.Clear();
            return Task.FromResult(released);
        }

        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionQueueStats(Rows.Count, null,
                Rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Count())));

        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
            CancellationToken cancellationToken = default)
        {
            var scoped = Rows.Where(r => projectId is null || r.ProjectId == projectId).ToList();
            return Task.FromResult(new PromotionWaitStats(scoped.Count, null, null,
                Rows.Select(r => r.ProjectId).Distinct(StringComparer.Ordinal).Count()));
        }

        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PruneRejectedAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionQueueOrphanReport(0, new Dictionary<string, int>()));
    }

    private sealed class RecordingShareStore : FakeMemoryStore
    {
        public List<string> SharedHashes { get; } = [];

        /// <summary>Hash -> exception ShareAsync throws for that hash instead of sharing it.</summary>
        public Dictionary<string, Exception> FailingHashes { get; } = new(StringComparer.Ordinal);

        public override Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public override Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            if (FailingHashes.TryGetValue(hash, out var exception))
            {
                throw exception;
            }

            SharedHashes.Add(hash);
            return Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, $"{hash}.md", "shared", "value", 0), true));
        }

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class SpyMetrics : IPromotionQueueMetrics
    {
        public List<(PromotionQueueStats Stats, int Capacity)> Snapshots { get; } = [];

        public void RecordEviction(string projectId, double victimScore, string reason) { }
        public void RecordPromoted(string projectId, double waitSeconds) { }
        public void RecordDiscarded(string projectId, double waitSeconds) { }
        public void RecordPruned(string projectId, int count) { }
        public void RecordFailed(string projectId, int count) { }
        public void RecordSnapshot(PromotionQueueStats stats, int capacity) => Snapshots.Add((stats, capacity));
    }
}
