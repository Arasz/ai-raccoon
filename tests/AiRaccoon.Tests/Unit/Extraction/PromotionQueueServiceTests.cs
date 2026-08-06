using AiRaccoon.Core;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     The propose tier end-to-end over real stores: propose persists + evicts at cap,
///     promote shares from the queue and drains, discard and meta reflect the queue, and
///     every path records metrics through the port.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueueServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue-svc");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _clock;
    private readonly RecordingMetrics _metrics;
    private readonly PromotionQueueService _service;

    public PromotionQueueServiceTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService());
        var queueStore = new SqlitePromotionQueueStore(_factory, _clock);
        _metrics = new RecordingMetrics();
        _service = new PromotionQueueService(queueStore, store, new UniformCountEvictionPolicy(),
            _metrics, NullLogger<PromotionQueueService>.Instance, _clock);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static QueueCandidate Candidate(string hash, string value, double score) =>
        new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

    private async Task SetCapAsync(int cap, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key = ExtractionConfigKeys.QueueCapacityGlobal, value = cap.ToString() });
    }

    [Fact]
    public async Task Propose_PersistsCandidates_AndReportsOutcome()
    {
        var outcome = await _service.ProposeAsync("acme",
            [Candidate("h1", "fact one", 1.5), Candidate("h2", "fact two", 2.0)],
            TestContext.Current.CancellationToken);

        outcome.Upserted.ShouldBe(2);
        outcome.Evicted.ShouldBeEmpty();
        var queued = await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken);
        queued.Count.ShouldBe(2);
        _metrics.QueuedDeltas.ShouldBe([("acme", 2)]);
    }

    [Fact]
    public async Task Propose_AtCap_EvictsLowestScoreFromGreatestCountProject()
    {
        await SetCapAsync(4, TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate("a1", "acme low", 1.0), Candidate("a2", "acme high", 3.0)],
            TestContext.Current.CancellationToken);
        await _service.ProposeAsync("other",
            [Candidate("o1", "other low", 1.0), Candidate("o2", "other high", 3.0)],
            TestContext.Current.CancellationToken);

        // At cap (4/4); acme and other tie at 2 rows — the new proposer's row pushes over.
        var outcome = await _service.ProposeAsync("third",
            [Candidate("t1", "third fact", 5.0)], TestContext.Current.CancellationToken);

        outcome.Evicted.ShouldHaveSingleItem();
        outcome.Evicted[0].Hash.ShouldBe("a1", "tie at 2 rows → ordinal-smallest project (acme), its lowest score");
        outcome.Evicted[0].Score.ShouldBe(1.0);
        outcome.Evicted[0].Reason.ShouldBe("capacity");

        var acme = await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken);
        acme.Select(r => r.Hash).ShouldBe(["a2"]);
        _metrics.Evictions.ShouldHaveSingleItem();
        _metrics.Evictions[0].ProjectId.ShouldBe("acme");
    }

    [Fact]
    public async Task Propose_EvictsUntilUnderCap_WhenInsertingMany()
    {
        await SetCapAsync(3, TestContext.Current.CancellationToken);
        var outcome = await _service.ProposeAsync("acme",
            [Candidate("h1", "one", 1.0), Candidate("h2", "two", 2.0),
             Candidate("h3", "three", 3.0), Candidate("h4", "four", 4.0), Candidate("h5", "five", 5.0)],
            TestContext.Current.CancellationToken);

        outcome.Evicted.Count.ShouldBe(2, "5 inserted into a cap of 3 → two evictions");
        outcome.Evicted.Select(e => e.Hash).ShouldBe(["h1", "h2"], "lowest scores first");
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe(["h5", "h4", "h3"]);
    }

    [Fact]
    public async Task Propose_DefaultsCap_WhenNoSetting()
    {
        var outcome = await _service.ProposeAsync("acme",
            Enumerable.Range(0, ExtractionConfigKeys.DefaultQueueCapacity + 5)
                .Select(i => Candidate($"h{i:000}", $"fact {i}", i))
                .ToList(),
            TestContext.Current.CancellationToken);

        outcome.Evicted.Count.ShouldBe(5);
        (await _service.GetMetaAsync(TestContext.Current.CancellationToken))
            .WaitingPromotionsCount.ShouldBe(ExtractionConfigKeys.DefaultQueueCapacity);
    }

    [Fact]
    public async Task Promote_SharesTopNFromTheQueue_AndDrains()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService());
        // Queue candidates are committed entries by construction (propose extracts from the
        // project context), so the hashes must exist there for ShareAsync.
        var low = await store.WriteAsync(new MemoryWriteRequest("acme", "low fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        var high = await store.WriteAsync(new MemoryWriteRequest("acme", "high fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        var mid = await store.WriteAsync(new MemoryWriteRequest("acme", "mid fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate(low.Hash, "low fact", 1.0), Candidate(high.Hash, "high fact", 5.0),
             Candidate(mid.Hash, "mid fact", 3.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.PromoteAsync(["acme"], 2, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe([high.Hash, mid.Hash], "top-2 by score");
        outcome.SkippedDuplicates.ShouldBe(0);
        outcome.RemainingByProject["acme"].ShouldBe(1);
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe([low.Hash]);
    }

    [Fact]
    public async Task Promote_SkipsAlreadySharedValues_AndDrainsThemToo()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService());
        var dup = await store.WriteAsync(new MemoryWriteRequest("acme", "shared fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        var fresh = await store.WriteAsync(new MemoryWriteRequest("acme", "fresh fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            // Seed the shared tier with the same value as the dup candidate.
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('shared-hash', 'shared/x.md', 'shared fact', 'shared', NULL, NULL, 1, 1, 'embedded')
                """);
        }

        await _service.ProposeAsync("acme",
            [Candidate(dup.Hash, "shared fact", 5.0), Candidate(fresh.Hash, "fresh fact", 4.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.PromoteAsync(["acme"], 10, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe([fresh.Hash]);
        outcome.SkippedDuplicates.ShouldBe(1);
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Discard_RemovesOneOrTheWholeProject()
    {
        await _service.ProposeAsync("acme",
            [Candidate("h1", "a", 1.0), Candidate("h2", "b", 2.0)],
            TestContext.Current.CancellationToken);

        (await _service.DiscardAsync("acme", "h1", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await _service.DiscardAsync("acme", null, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        _metrics.QueuedDeltas.Sum(d => d.Delta).ShouldBe(0, "2 upserted, 2 discarded");
    }

    [Fact]
    public async Task GetMeta_ReflectsTheQueue()
    {
        (await _service.GetMetaAsync(TestContext.Current.CancellationToken))
            .ShouldBe(new ResponseMeta(0, null, null));

        await _service.ProposeAsync("acme", [Candidate("h1", "fact", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var meta = await _service.GetMetaAsync(TestContext.Current.CancellationToken);
        meta.WaitingPromotionsCount.ShouldBe(1);
        meta.PromotionsWaitTimeSeconds.ShouldBe(30);
        meta.WaitingByProject.ShouldBe(new Dictionary<string, int> { ["acme"] = 1 });
    }

    [Fact]
    public async Task Sweep_NeverTouchesTheQueue()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService());
        var doomed = await store.WriteAsync(
            new MemoryWriteRequest("acme", "doomed entry", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        await store.SetEntryTtlAsync("acme", doomed.Hash, 0.001, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(1)); // older than the TTL, rating below any threshold
        await _service.ProposeAsync("acme",
            [Candidate(doomed.Hash, "doomed entry", 1.0), Candidate("fake-hash", "queued row", 2.0)],
            TestContext.Current.CancellationToken);

        var sweeper = new SweepService(store, _clock);
        await sweeper.SweepAsync("acme", 0.9, dryRun: false, TestContext.Current.CancellationToken);

        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe(["fake-hash", doomed.Hash],
            "sweep degrades committed entries only — the propose tier is untouched");
    }

    private sealed class RecordingMetrics : IPromotionQueueMetrics
    {
        public List<(string ProjectId, int Delta)> QueuedDeltas { get; } = [];
        public List<(string ProjectId, double Score, string Reason)> Evictions { get; } = [];
        public List<(string ProjectId, double Wait)> Promoted { get; } = [];
        public List<double> Utilization { get; } = [];

        public void RecordQueued(string projectId, int delta) => QueuedDeltas.Add((projectId, delta));
        public void RecordEviction(string projectId, double victimScore, string reason) =>
            Evictions.Add((projectId, victimScore, reason));
        public void RecordPromoted(string projectId, double waitSeconds) => Promoted.Add((projectId, waitSeconds));
        public void RecordDiscarded(string projectId, double waitSeconds) { }
        public void RecordUtilization(double ratio) => Utilization.Add(ratio);
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
