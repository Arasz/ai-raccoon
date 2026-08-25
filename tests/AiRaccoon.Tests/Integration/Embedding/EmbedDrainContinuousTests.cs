using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP1 (docs/work/2026-08-23-post-delta-4-plan.md WP12-B): a drain pass that consumes a full
///     row budget means the backlog isn't necessarily empty, so the drain re-signals its own topic
///     instead of waiting out the 15s on-demand poll (<see cref="EmbedDrainService.DrainOnceAsync" />).
///     <see cref="ICodeEmbedder.EmbedPendingBatchAsync" />'s count reflects rows whose UPDATE
///     landed, so a full budget is real progress and the re-signal cannot spin.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedDrainContinuousTests : IDisposable
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-drain-continuous");
    private readonly SqliteConnectionFactory _factory;

    public EmbedDrainContinuousTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private EmbedDrainService NewService(IEventPump<EmbedDrainRequest> pump, ICodeEmbedder code,
        ILogger<EmbedDrainService>? logger = null) =>
        new(pump, _factory, new NoOpEntryEmbedder(), code, new SqliteSettingsStore(_factory),
            NoOpMeasurementRecorder.Instance, TimeProvider.System, TestTelemetry.None,
            logger ?? NullLogger<EmbedDrainService>.Instance);

    /// <summary>For a clean backlog (an exact multiple of the row budget), one signal drains it in
    /// N passes: the first two passes each consume exactly the full row budget and re-signal
    /// themselves; the third finds fewer rows than the budget and stops.</summary>
    [RetryFact]
    public async Task FullBudgetPasses_ReSignalUntilTheBacklogRunsOut()
    {
        var pump = TestData.NewEmbedDrainPump();
        var code = new SequencedCodeEmbedder(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun, 0);
        var service = NewService(pump, code);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        (await service.Drains.WaitAsync(3, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "a full-budget pass must re-signal itself so the backlog drains without waiting on the poll");

        code.Calls.ShouldBe([
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun
        ], "the row budget passed as `limit` is re-read and re-applied on every pass, including the one that returns fewer rows than it asked for");
        pump.EnqueuedCount.ShouldBe(3, "the original signal plus one self re-signal per full-budget pass");
        pump.CoalescedCount.ShouldBe(0, "each self re-signal fires only after the prior item was taken, so nothing collides");
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.ExecuteTask!.IsFaulted.ShouldBeFalse("a per-pass exception must never fault the hosted service");
    }

    /// <summary>A pass that returns fewer rows than the budget must not re-signal — the backlog is
    /// exhausted, so a further signal would just spin on an empty corpus.</summary>
    [RetryFact]
    public async Task PartialBudgetPass_DoesNotReSignal()
    {
        var pump = TestData.NewEmbedDrainPump();
        var code = new SequencedCodeEmbedder(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun - 1);
        var service = NewService(pump, code);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        pump.DrainUpTo(1).ShouldBeEmpty("a partial-budget pass must not queue its own next signal");
        pump.EnqueuedCount.ShouldBe(1);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.ExecuteTask!.IsFaulted.ShouldBeFalse("a per-pass exception must never fault the hosted service");
    }

    /// <summary>
    ///     F2 (review round on #530): the plan's acceptance bullet "a re-signal for an
    ///     already-queued corpus coalesces" needs the deterministic case that actually exercises
    ///     it, not just a negative assertion that it never happened. The fake enqueues the SAME
    ///     corpus mid-drain — from inside <see cref="ICodeEmbedder.EmbedPendingBatchAsync" />,
    ///     after the request has already been taken off the pump but before the pass returns — so
    ///     when <c>DrainOnceAsync</c>'s own full-budget re-signal fires afterward, it finds that
    ///     request already queued and coalesces against it instead of queueing a second one.
    /// </summary>
    [RetryFact]
    public async Task ReSignalForAnAlreadyQueuedCorpus_Coalesces()
    {
        var pump = TestData.NewEmbedDrainPump();
        var request = new EmbedDrainRequest(EmbedCorpus.Code);
        var code = new SelfEnqueuingCodeEmbedder(pump, request, BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
        var service = NewService(pump, code);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        pump.TryEnqueue(request).ShouldBeTrue();

        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        pump.EnqueuedCount.ShouldBe(2, "the initial signal plus the fake's own mid-drain enqueue for the same corpus");
        pump.CoalescedCount.ShouldBe(1,
            "the production re-signal for that already-queued corpus must coalesce, not queue a second item");
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.ExecuteTask!.IsFaulted.ShouldBeFalse("a per-pass exception must never fault the hosted service");
    }

    /// <summary>
    ///     NIT from the same review round: the self re-signal's <c>TryEnqueue</c> result was
    ///     dropped, so a re-signal that coalesces (or, in principle, is capacity-dropped) left no
    ///     trace. Same scenario as <see cref="ReSignalForAnAlreadyQueuedCorpus_Coalesces" />, plus
    ///     the debug-level log.
    /// </summary>
    [RetryFact]
    public async Task SelfReSignal_NotQueued_LogsAtDebug()
    {
        var pump = TestData.NewEmbedDrainPump();
        var request = new EmbedDrainRequest(EmbedCorpus.Code);
        var code = new SelfEnqueuingCodeEmbedder(pump, request, BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(pump, code, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        pump.TryEnqueue(request).ShouldBeTrue();

        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1007)
            .ShouldBe(1, "the dropped self re-signal must be visible, not silently swallowed");
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Returns a scripted sequence of row counts, one per call (the last value repeats
    /// past the end). <see cref="Calls" /> pins the <c>limit</c> each call actually received, not
    /// what this fake chose to return.</summary>
    private sealed class SequencedCodeEmbedder(params int[] rowsSequence) : ICodeEmbedder
    {
        private readonly Lock _gate = new();
        private int _next;

        public List<int> Calls { get; } = [];

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken)
        {
            int rows;
            lock (_gate)
            {
                rows = _next < rowsSequence.Length ? rowsSequence[_next] : rowsSequence[^1];
                _next++;
                Calls.Add(limit);
            }

            return Task.FromResult(rows);
        }

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Enqueues <paramref name="request" /> itself, once, mid-call, then returns a full
    /// budget — sets up the window for <c>DrainOnceAsync</c>'s own re-signal to coalesce against
    /// it. Every later call returns 0.</summary>
    private sealed class SelfEnqueuingCodeEmbedder(IEventPump<EmbedDrainRequest> pump, EmbedDrainRequest request,
        int rowsPerRun) : ICodeEmbedder
    {
        private readonly Lock _gate = new();
        private bool _enqueued;

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_enqueued)
                {
                    return Task.FromResult(0);
                }

                _enqueued = true;
            }

            pump.TryEnqueue(request);
            return Task.FromResult(rowsPerRun);
        }

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpEntryEmbedder : IEntryEmbedder
    {
        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
}
