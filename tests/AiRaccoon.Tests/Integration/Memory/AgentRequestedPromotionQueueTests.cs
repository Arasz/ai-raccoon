using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Memory;

/// <summary>
///     F22 and F25 against the REAL promotion queue — SqlitePromotionQueueStore + MemoryWriteService +
///     SharedExtractionRunner. SharedWriteIsAPromotionRequestTests records what the write path decides
///     through a FakePromotionQueue, so its queue assertions pass vacuously; this class must never
///     construct that fake, or both gates lose the real store interaction they exist to pin.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class AgentRequestedPromotionQueueTests : IDisposable
{
    private const string ProjectId = "acme";
    private const string Content = "The batch-eleven fix is staged for review.";

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("agent-requested-promotion");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqlitePromotionQueueStore _queueStore;
    private readonly PromotionQueueService _queue;
    private readonly MemoryWriteService _writes;
    private readonly SharedExtractionRunner _runner;

    public AgentRequestedPromotionQueueTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var clock = new FakeTimeProvider(FixedNow);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), clock,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _queueStore = new SqlitePromotionQueueStore(_factory, clock);
        _queue = new PromotionQueueService(_queueStore, store, new UniformCountEvictionPolicy(),
            new NoopMetrics(), NullLogger<PromotionQueueService>.Instance, clock);
        _writes = new MemoryWriteService(store, _queue);
        _runner = new SharedExtractionRunner(store, new SharedExtractionService(), _queue, clock);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     F22 gate (a), red today: the write path leaves ScorerVersion at its 0 default, so the next
    ///     propose pass's <c>ClearStaleAsync(projectId, PromotionScorer.Version = 2)</c> deletes the row
    ///     and `count(*)` drops from 1 to 0. The bank holds no embedding engine, so the entry is not an
    ///     extraction candidate (embed_state 'pending') and nothing re-admits it — exactly the terminal
    ///     case F22 measured. K2 stamps instead: the row survives and any re-score is honest.
    /// </summary>
    [RetryFact]
    public async Task AgentRequestedRow_SurvivesAProposePass_StampedWithTheCurrentScorerVersion()
    {
        var entry = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content) { Context = ContextNaming.SharedContext },
            TestContext.Current.CancellationToken);
        entry.Stored.ShouldBeTrue();

        await _runner.ProposeAsync(ProjectId, new SharedIndex([], []), includeTtlRows: false, limit: 20,
            cancellationToken: TestContext.Current.CancellationToken);

        var surviving = await _queueStore.ListAsync(ProjectId, TestContext.Current.CancellationToken);
        surviving.Count.ShouldBe(1, "the propose pass's stale-scorer clear must not destroy an agent-requested row");
        surviving[0].Hash.ShouldBe(entry.Hash);
        surviving[0].Reasons.ShouldBe([PromotionReasons.AgentRequestedShare]);
        surviving[0].ScorerVersion.ShouldBe(PromotionScorer.Version,
            "K2: an agent-requested candidate is stamped with the current scorer version, so ClearStale keeps it");
    }

    /// <summary>
    ///     F25 gate (b), red today: <c>PromotionQueueSql.Upsert</c> refuses a hash with a remembered
    ///     discard, so nothing is enqueued — but MemoryWriteService sets the reason unconditionally
    ///     and the response still claims "queued-for-promotion".
    /// </summary>
    [RetryFact]
    public async Task RewriteAfterDiscard_ReportsRefusal_AndLeavesTheQueueEmpty()
    {
        var entry = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content) { Context = ContextNaming.SharedContext },
            TestContext.Current.CancellationToken);
        (await _queueStore.ListAsync(ProjectId, TestContext.Current.CancellationToken)).Count.ShouldBe(1);

        var discarded = await _queue.DiscardAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken);
        discarded.ShouldBe(1);

        var rewritten = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content) { Context = ContextNaming.SharedContext },
            TestContext.Current.CancellationToken);

        rewritten.Stored.ShouldBeTrue("the project row still lands; only the promotion request is refused");
        rewritten.Reason.ShouldBe("not-queued: agent-requested-share refused (discarded earlier or already shared)",
            "F25: the queue refused the upsert, so the response must not claim a queued review");
        rewritten.Reason!.ShouldNotContain("queued-for-promotion");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var queued = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM promotion_queue WHERE project_id = @ProjectId",
                new { ProjectId }, cancellationToken: TestContext.Current.CancellationToken));
        queued.ShouldBe(0);
    }

    private sealed class NoopMetrics : IPromotionQueueMetrics
    {
        public void RecordEviction(string projectId, double victimScore, string reason)
        {
        }

        public void RecordPromoted(string projectId, double waitSeconds)
        {
        }

        public void RecordDiscarded(string projectId, double waitSeconds)
        {
        }

        public void RecordPruned(string projectId, int count)
        {
        }

        public void RecordFailed(string projectId, int count)
        {
        }

        public void RecordSnapshot(PromotionQueueStats stats, int capacity)
        {
        }
    }
}