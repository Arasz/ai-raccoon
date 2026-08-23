using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP11-B2 (docs/work/2026-08-22-post-delta-3-plan.md, owner ruling G17): the embed topic's
///     single consumer. Fakes for <see cref="IEntryEmbedder" />/<see cref="ICodeEmbedder" /> — no
///     real ONNX — but a real bank connection (opening one is cheap; the fakes never touch its
///     content), matching this suite's neighbours (PendingEmbedJobTests, CodeReindexJobTests).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedDrainServiceTests : IDisposable
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-drain-service");
    private readonly SqliteConnectionFactory _factory;

    public EmbedDrainServiceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private EmbedDrainService NewService(IEventPump<EmbedDrainRequest> pump, IEntryEmbedder? entry = null,
        ICodeEmbedder? code = null) =>
        new(pump, _factory, entry ?? new RecordingEntryEmbedder(), code ?? new RecordingCodeEmbedder(),
            new SqliteSettingsStore(_factory), NoOpMeasurementRecorder.Instance, TimeProvider.System,
            TestTelemetry.None, NullLogger<EmbedDrainService>.Instance);

    /// <summary>E1.</summary>
    [Fact]
    public async Task OneSignal_RunsExactlyOneDrainOfTheRowBudget()
    {
        var pump = TestData.NewEmbedDrainPump();
        var entry = new RecordingEntryEmbedder();
        var service = NewService(pump, entry: entry);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        entry.Calls.ShouldHaveSingleItem().ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>E2: ten enqueues while a drain is held open coalesce to at most one further pass.</summary>
    [Fact]
    public async Task ManySignalsForOneCorpus_CoalesceToAtMostOneFurtherDrain()
    {
        var pump = TestData.NewEmbedDrainPump();
        var entry = new RecordingEntryEmbedder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.Gate = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var service = NewService(pump, entry: entry);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        await entered.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        for (var i = 0; i < 10; i++)
        {
            pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
        }

        pump.CoalescedCount.ShouldBe(9, "the first of the ten claims the coalesce key; the rest collapse into it");
        release.SetResult();
        (await service.Drains.WaitAsync(2, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        entry.Calls.Count.ShouldBeLessThanOrEqualTo(2);
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>E3: a fake records the max observed concurrent entrants across BOTH corpora — the
    /// single reader means it can never exceed 1, by construction.</summary>
    [Fact]
    public async Task MemoryAndCodeSignals_NeverOverlapInTheEmbedder()
    {
        var pump = TestData.NewEmbedDrainPump();
        var probe = new ConcurrencyProbe();
        var entry = new RecordingEntryEmbedder(probe);
        var code = new RecordingCodeEmbedder(probe);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.Gate = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var service = NewService(pump, entry, code);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        await entered.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        release.SetResult();
        (await service.Drains.WaitAsync(2, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        probe.MaxConcurrent.ShouldBe(1);
        entry.Calls.ShouldHaveSingleItem();
        code.Calls.ShouldHaveSingleItem();
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>E4.</summary>
    [Fact]
    public async Task DrainThrows_LoopSurvives_AndTheNextSignalStillDrains()
    {
        var pump = TestData.NewEmbedDrainPump();
        var entry = new RecordingEntryEmbedder { ThrowOnce = new InvalidOperationException("drain boom") };
        var service = NewService(pump, entry: entry);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        (await service.Drains.WaitAsync(2, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        entry.Calls.Count.ShouldBe(2);
        run.IsFaulted.ShouldBeFalse("a per-pass exception must never fault the hosted service");
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>E5 (WP1, docs/work/2026-08-23-post-delta-4-plan.md): a partial-budget drain does
    /// not re-signal — only a full row budget does (see <c>EmbedDrainContinuousTests</c> for that
    /// half), so the pump stays empty until an external producer signals again.</summary>
    [Fact]
    public async Task PartialRows_NoSelfReEnqueue_TheNextSignalDoesTheRest()
    {
        var pump = TestData.NewEmbedDrainPump();
        var entry = new RecordingEntryEmbedder { RowsToReturn = BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun - 1 };
        var service = NewService(pump, entry: entry);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        pump.DrainUpTo(1).ShouldBeEmpty("a partial-budget drain must not queue its own next pass");
        pump.EnqueuedCount.ShouldBe(1);
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     E11, adapted to this topic's actual shape: item space is 2 (Memory|Code) and the topic's
    ///     capacity is 8 — a capacity-based drop (EventPump's <c>DroppedCount</c>) is structurally
    ///     unreachable here (8 &gt; 2, so the reservation counter can never be exceeded). The
    ///     equivalent "signal did not make it into the pump" case for a coalescing 2-value topic is
    ///     a coalesced signal, and the recovery argument is identical: the row stays
    ///     <c>embed_state='pending'</c> (the durable outbox, ADR-0076), so <c>HasWorkAsync</c> is
    ///     still true and the next poll's enqueue (once the coalesced-away duplicate's target is
    ///     taken) drains it.
    /// </summary>
    [Fact]
    public async Task CoalescedSignal_IsRecoveredByTheNextPoll()
    {
        var pump = TestData.NewEmbedDrainPump();
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();

        // A second signal for the same corpus while the first is still queued (not yet taken)
        // coalesces away — nothing observes it landed.
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeFalse();
        pump.CoalescedCount.ShouldBe(1);

        // The durable record (embed_state='pending') is what HasWorkAsync reads, not the pump —
        // a fresh PendingEmbedJob poll enqueues again once the queued item is taken and drained.
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedPendingProviderAndRowAsync(connection);
        var job = new PendingEmbedJob(new EntryEmbedder(new CountingEmbeddingService(),
            NSubstitute.Substitute.For<IModelMigrationLease>(), TimeProvider.System), pump);
        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the coalesced signal cost nothing durable — the row is still pending");
    }

    private static async Task SeedPendingProviderAndRowAsync(SqliteConnection connection)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
            VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
            """,
            new { hash = Guid.NewGuid().ToString("N"), path = "p.md", value = "pending" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Coalescing test: same corpus queued twice collapses to one item; different corpora queue separately.</summary>
    [Fact]
    public void TryEnqueue_SameCorpusTwice_CoalescesToOneItem_DifferentCorpora_QueueBoth()
    {
        var pump = TestData.NewEmbedDrainPump();

        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeTrue();
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)).ShouldBeFalse();
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        pump.EnqueuedCount.ShouldBe(2);
        pump.CoalescedCount.ShouldBe(1);
        var drained = pump.DrainUpTo(10);
        drained.Count.ShouldBe(2);
        drained.ShouldContain(r => r.Corpus == EmbedCorpus.Memory);
        drained.ShouldContain(r => r.Corpus == EmbedCorpus.Code);
    }

    /// <summary>
    ///     The parallel-producer invariant the #490 reviewer asked for: every attempted enqueue is
    ///     accounted for exactly once, across concurrent producers.
    /// </summary>
    [Fact]
    public async Task ParallelProducers_EveryAttemptIsAccountedForExactlyOnce()
    {
        var pump = TestData.NewEmbedDrainPump();
        const int producers = 64;

        await Task.WhenAll(Enumerable.Range(0, producers).Select(i => Task.Run(() =>
            pump.TryEnqueue(new EmbedDrainRequest(i % 2 == 0 ? EmbedCorpus.Memory : EmbedCorpus.Code)))));

        (pump.EnqueuedCount + pump.DroppedCount + pump.CoalescedCount).ShouldBe(producers);
    }

    private sealed class ConcurrencyProbe
    {
        private readonly Lock _gate = new();
        private int _current;

        public int MaxConcurrent { get; private set; }

        public IDisposable Enter()
        {
            lock (_gate)
            {
                _current++;
                MaxConcurrent = Math.Max(MaxConcurrent, _current);
            }

            return new Exit(this);
        }

        private void Leave()
        {
            lock (_gate)
            {
                _current--;
            }
        }

        private sealed class Exit(ConcurrencyProbe probe) : IDisposable
        {
            public void Dispose() => probe.Leave();
        }
    }

    private sealed class RecordingEntryEmbedder(ConcurrencyProbe? probe = null) : IEntryEmbedder
    {
        public List<int> Calls { get; } = [];

        public int RowsToReturn { get; set; } = 1;

        public Func<Task>? Gate { get; set; }

        public Exception? ThrowOnce { get; set; }

        private bool _thrown;

        public async Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken)
        {
            using var scope = probe?.Enter();
            lock (Calls)
            {
                Calls.Add(limit);
            }

            if (ThrowOnce is { } ex && !_thrown)
            {
                _thrown = true;
                throw ex;
            }

            if (Gate is not null)
            {
                await Gate().ConfigureAwait(false);
            }

            return RowsToReturn;
        }

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingCodeEmbedder(ConcurrencyProbe? probe = null) : ICodeEmbedder
    {
        public List<int> Calls { get; } = [];

        public int RowsToReturn { get; set; } = 1;

        public Func<Task>? Gate { get; set; }

        public async Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken)
        {
            using var scope = probe?.Enter();
            lock (Calls)
            {
                Calls.Add(limit);
            }

            if (Gate is not null)
            {
                await Gate().ConfigureAwait(false);
            }

            return RowsToReturn;
        }

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
