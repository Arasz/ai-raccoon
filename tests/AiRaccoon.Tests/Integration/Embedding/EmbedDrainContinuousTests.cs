using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

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

    private EmbedDrainService NewService(IEventPump<EmbedDrainRequest> pump, ICodeEmbedder code) =>
        new(pump, _factory, new NoOpEntryEmbedder(), code, new SqliteSettingsStore(_factory),
            TestTelemetry.None, NullLogger<EmbedDrainService>.Instance);

    /// <summary>One signal drains a backlog of N·budget rows in N passes: the first two passes
    /// each consume exactly the full row budget and re-signal themselves; the third finds fewer
    /// rows than the budget and stops.</summary>
    [Fact]
    public async Task FullBudgetPasses_ReSignalUntilTheBacklogRunsOut()
    {
        var pump = TestData.NewEmbedDrainPump();
        var code = new SequencedCodeEmbedder(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun, 0);
        var service = NewService(pump, code);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        (await service.Drains.WaitAsync(3, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue(
            "a full-budget pass must re-signal itself so the backlog drains without waiting on the poll");

        code.Calls.ShouldBe([
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun,
            0
        ]);
        pump.EnqueuedCount.ShouldBe(3, "the original signal plus one self re-signal per full-budget pass");
        pump.CoalescedCount.ShouldBe(0, "each self re-signal fires only after the prior item was taken, so nothing collides");
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A pass that returns fewer rows than the budget must not re-signal — the backlog is
    /// exhausted, so a further signal would just spin on an empty corpus.</summary>
    [Fact]
    public async Task PartialBudgetPass_DoesNotReSignal()
    {
        var pump = TestData.NewEmbedDrainPump();
        var code = new SequencedCodeEmbedder(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun - 1);
        var service = NewService(pump, code);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code)).ShouldBeTrue();

        (await service.Drains.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        pump.DrainUpTo(1).ShouldBeEmpty("a partial-budget pass must not queue its own next signal");
        pump.EnqueuedCount.ShouldBe(1);
        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class SequencedCodeEmbedder(params int[] rowsSequence) : ICodeEmbedder
    {
        private int _next;

        public List<int> Calls { get; } = [];

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(limit);
            }

            var rows = _next < rowsSequence.Length ? rowsSequence[_next] : rowsSequence[^1];
            _next++;
            return Task.FromResult(rows);
        }

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
