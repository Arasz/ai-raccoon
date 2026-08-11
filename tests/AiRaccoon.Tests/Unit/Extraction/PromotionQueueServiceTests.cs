using System.Text.Json;
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
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        var queueStore = new SqlitePromotionQueueStore(_factory, _clock);
        _metrics = new RecordingMetrics();
        _service = new PromotionQueueService(queueStore, store, new UniformCountEvictionPolicy(),
            _metrics, NullLogger<PromotionQueueService>.Instance, _clock);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static QueueCandidate Candidate(string hash, string value, double score) =>
        new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

    private Task<PromotionMeta> MetaFor(string? projectId) =>
        _service.GetMetaAsync(projectId, TestContext.Current.CancellationToken);

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static string Json(PromotionMeta meta) => JsonSerializer.Serialize(meta, WireJson);

    /// <summary>Every property path in the serialized meta — what the envelope costs in wire shape.</summary>
    private static IReadOnlyList<string> Shape(PromotionMeta meta)
    {
        using var document = JsonDocument.Parse(Json(meta));
        var paths = new List<string>();
        Collect(document.RootElement, string.Empty, paths);
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static void Collect(JsonElement element, string prefix, List<string> paths)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            paths.Add(prefix + property.Name);
            Collect(property.Value, $"{prefix}{property.Name}.", paths);
        }
    }

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
        _metrics.Snapshots.ShouldHaveSingleItem().Stats.PerProject["acme"].ShouldBe(2);
    }

    [Fact]
    public async Task Propose_ReProposingTheSameHash_DoesNotGrowTheRealQueueSize()
    {
        await _service.ProposeAsync("acme", [Candidate("h1", "fact one", 1.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.ProposeAsync("acme",
            [Candidate("h1", "fact one refreshed", 2.0)], TestContext.Current.CancellationToken);

        outcome.Upserted.ShouldBe(0, "h1 already occupied a queue slot; the queue did not grow");
        _metrics.Snapshots.Select(s => s.Stats.PerProject["acme"]).ShouldBe([1, 1],
            "RecordSnapshot must report the real persisted queue size, not the SQLite conflict-update count");
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
        (await MetaFor("acme")).WaitingPromotionsCount.ShouldBe(ExtractionConfigKeys.DefaultQueueCapacity);
    }

    [Fact]
    public async Task Promote_SharesTopNFromTheQueue_AndDrains()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
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

    /// <summary>The layered dedup contract (docs/adr/0026): propose refuses EXACT shared twins at
    /// upsert; a whitespace twin still queues (exact values differ) and promote skips it via its
    /// NORMALIZED twin check — so skip accounting survives with the persistence-layer refusal.</summary>
    [Fact]
    public async Task Promote_SkipsAlreadySharedValues_AndDrainsThemToo()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        var dup = await store.WriteAsync(new MemoryWriteRequest("acme", "shared  fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        var fresh = await store.WriteAsync(new MemoryWriteRequest("acme", "fresh fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            // Seed the shared tier with a whitespace twin of the dup candidate's value.
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('shared-hash', 'shared/x.md', 'shared fact', 'shared', NULL, NULL, 1, 1, 'embedded')
                """);
        }

        await _service.ProposeAsync("acme",
            [Candidate(dup.Hash, "shared  fact", 5.0), Candidate(fresh.Hash, "fresh fact", 4.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.PromoteAsync(["acme"], 10, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe([fresh.Hash]);
        outcome.SkippedDuplicates.ShouldBe(1);
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ clear stale (ADR-0018)
    [Fact]
    public async Task ClearStaleAsync_RemovesRowsOnAnOlderScorerVersion_ReportsTheCount()
    {
        await _service.ProposeAsync("acme",
            [new QueueCandidate("stale", "stale.md", "old value", null, 2.5, [], ScorerVersion: 0),
             new QueueCandidate("current", "current.md", "new value", null, 1.0, [], ScorerVersion: 1)],
            TestContext.Current.CancellationToken);

        var cleared = await _service.ClearStaleAsync("acme", currentScorerVersion: 1, TestContext.Current.CancellationToken);

        cleared.ShouldBe(1);
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe(["current"]);
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
        _metrics.Snapshots[^1].Stats.TotalCount.ShouldBe(0, "2 upserted, 2 discarded — the queue is back to empty");
    }

    [Fact]
    public async Task Discard_RecordsDiscarded_WithTheSameWaitShapeAsPromote()
    {
        await _service.ProposeAsync("acme", [Candidate("h1", "a", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(45));

        await _service.DiscardAsync("acme", "h1", TestContext.Current.CancellationToken);

        _metrics.Discarded.ShouldBe([("acme", 45.0)],
            "RecordDiscarded must fire on the discard path, the same way RecordPromoted fires on the promote path — " +
            "otherwise ai_raccoon.queue.discarded ships permanently flat");
    }

    /// <summary>C5: the envelope carries the asking project's queue state only — another project's
    /// id or counts never ride along on an unrelated tool call.</summary>
    [Fact]
    public async Task GetMeta_ReportsTheAskingProjectOnly()
    {
        await _service.ProposeAsync("acme", [Candidate("a1", "a1", 1.0), Candidate("a2", "a2", 2.0)],
            TestContext.Current.CancellationToken);
        await _service.ProposeAsync("other",
            [Candidate("o1", "o1", 1.0), Candidate("o2", "o2", 2.0), Candidate("o3", "o3", 3.0)],
            TestContext.Current.CancellationToken);

        var meta = await MetaFor("acme");

        meta.WaitingPromotionsCount.ShouldBe(2);
        Json(meta).ShouldNotContain("other", customMessage: "another project's queue is not this caller's business");
    }

    /// <summary>C5: the envelope is bounded by construction — one project, always — not by a tuned cap.</summary>
    [Fact]
    public async Task GetMeta_ShapeDoesNotGrowWithTheProjectCount()
    {
        await _service.ProposeAsync("acme", [Candidate("a1", "a1", 1.0)],
            TestContext.Current.CancellationToken);
        var alone = Shape(await MetaFor("acme"));

        foreach (var other in (string[])["b", "c", "d", "e", "f"])
        {
            await _service.ProposeAsync(other, [Candidate($"{other}1", other, 1.0)],
                TestContext.Current.CancellationToken);
        }

        Shape(await MetaFor("acme")).ShouldBe(alone);
    }

    /// <summary>PromotionMeta's contract: zero is informative, never absent.</summary>
    [Fact]
    public async Task GetMeta_ReportsZero_WhenTheAskingProjectHasNothingQueued()
    {
        await _service.ProposeAsync("other", [Candidate("o1", "o1", 1.0)],
            TestContext.Current.CancellationToken);

        var meta = await MetaFor("acme");

        meta.WaitingPromotionsCount.ShouldBe(0);
        Json(meta).ShouldContain("\"waitingPromotionsCount\":0");
    }

    [Fact]
    public async Task GetMeta_WaitAges_AreTheAskingProjectsOwn()
    {
        await _service.ProposeAsync("other", [Candidate("o1", "stale", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(30));
        await _service.ProposeAsync("acme", [Candidate("a1", "fresh", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var meta = await MetaFor("acme");

        meta.PromotionsWaitTimeSeconds.ShouldBe(10);
        meta.OldestWaitSeconds.ShouldBe(10, "another project's 30-day-old row is not this project's wait");
    }

    [Fact]
    public async Task GetMeta_ReflectsTheQueue()
    {
        (await MetaFor("acme")).ShouldBe(new PromotionMeta(0, null));

        await _service.ProposeAsync("acme", [Candidate("h1", "fact", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var meta = await MetaFor("acme");
        meta.WaitingPromotionsCount.ShouldBe(1);
        meta.PromotionsWaitTimeSeconds.ShouldBe(30);
    }

    /// <summary>B1: nothing drains a propose-only queue, so a stale row needs to be visible even
    /// when the average wait looks fine — the response meta is the surface every tool already carries.</summary>
    [Fact]
    public async Task GetMeta_SurfacesTheOldestWaitSeparatelyFromTheAverage()
    {
        await _service.ProposeAsync("acme", [Candidate("stale", "old fact", 1.0)],
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(30));
        await _service.ProposeAsync("acme", [Candidate("fresh", "new fact", 2.0)],
            TestContext.Current.CancellationToken);

        var meta = await MetaFor("acme");

        meta.OldestWaitSeconds.ShouldNotBeNull();
        meta.OldestWaitSeconds!.Value.ShouldBe(TimeSpan.FromDays(30).TotalSeconds, 0.1);
        meta.PromotionsWaitTimeSeconds.ShouldNotBe(meta.OldestWaitSeconds,
            "the average across both rows must not equal the single stalest row's age");
    }

    [Fact]
    public async Task GetMeta_SurfacesTheAskingProjectsCapacity()
    {
        await SetCapAsync(100, TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate("a1", "a1", 1.0), Candidate("a2", "a2", 2.0), Candidate("a3", "a3", 3.0)],
            TestContext.Current.CancellationToken);
        await _service.ProposeAsync("other",
            [Candidate("o1", "o1", 1.0)], TestContext.Current.CancellationToken);

        (await MetaFor("acme")).Capacity
            .ShouldBe(new PromotionCapacityInfo(Reserved: 50, Used: 3, Borrowing: false),
                "two projects occupy a cap of 100 — this project's own share, not everyone's");
    }

    [Fact]
    public async Task GetMeta_EmptyQueue_HasNoCapacityInfo()
    {
        (await MetaFor("acme")).Capacity.ShouldBeNull();
    }

    /// <summary>memory_promotion_list may name no project; its meta is the bank-wide count — still a scalar.</summary>
    [Fact]
    public async Task GetMeta_Unscoped_CountsTheWholeBank_WithoutNamingProjects()
    {
        await _service.ProposeAsync("acme", [Candidate("a1", "a1", 1.0)],
            TestContext.Current.CancellationToken);
        await _service.ProposeAsync("other", [Candidate("o1", "o1", 1.0)],
            TestContext.Current.CancellationToken);

        var meta = await MetaFor(null);

        meta.WaitingPromotionsCount.ShouldBe(2);
        meta.Capacity.ShouldBeNull("a reservation belongs to a project, and this call named none");
        Json(meta).ShouldNotContain("other");
    }

    /// <summary>
    ///     Sweep degrades committed entries only, never the queue directly — but the
    ///     promotion_queue_entries_ad trigger (ADR-0023) fires on the entries delete sweep does,
    ///     so a candidate whose entry sweep just removed cannot survive it. A candidate with no
    ///     backing entry, or one backed by a healthy entry sweep never touches, is untouched.
    /// </summary>
    [Fact]
    public async Task Sweep_DropsQueueRowsForTheEntriesItDeleted_AndLeavesTheRest()
    {
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        var doomed = await store.WriteAsync(
            new MemoryWriteRequest("acme", "doomed entry", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        await store.SetEntryTtlAsync("acme", doomed.Hash, 1, TestContext.Current.CancellationToken);
        var healthy = await store.WriteAsync(
            new MemoryWriteRequest("acme", "healthy entry", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(2)); // older than the TTL, rating below any threshold
        await _service.ProposeAsync("acme",
            [Candidate(doomed.Hash, "doomed entry", 1.0), Candidate("fake-hash", "queued row", 2.0),
             Candidate(healthy.Hash, "healthy entry", 3.0)],
            TestContext.Current.CancellationToken);

        var sweeper = new SweepService(store, _clock);
        await sweeper.SweepAsync("acme", 0.9, dryRun: false, TestContext.Current.CancellationToken);

        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe([healthy.Hash, "fake-hash"],
            "the doomed entry's row is gone with it; a candidate with no backing entry and one backed " +
            "by an entry sweep left alone both survive");
    }

    private sealed class RecordingMetrics : IPromotionQueueMetrics
    {
        public List<(string ProjectId, double Score, string Reason)> Evictions { get; } = [];
        public List<(string ProjectId, double Wait)> Promoted { get; } = [];
        public List<(string ProjectId, double Wait)> Discarded { get; } = [];
        public List<(PromotionQueueStats Stats, int Capacity)> Snapshots { get; } = [];

        public void RecordEviction(string projectId, double victimScore, string reason) =>
            Evictions.Add((projectId, victimScore, reason));
        public void RecordPromoted(string projectId, double waitSeconds) => Promoted.Add((projectId, waitSeconds));
        public void RecordDiscarded(string projectId, double waitSeconds) => Discarded.Add((projectId, waitSeconds));
        public void RecordSnapshot(PromotionQueueStats stats, int capacity) => Snapshots.Add((stats, capacity));
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
