using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Observability;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     `chunk-backfill` writes its pieces unembedded, and (before PendingEmbedJob existed) the
///     pass's one pending-embed sweep ran BEFORE the job list — so on a real bank the backfill's
///     13,578 new rows sat pending, waiting for the next pass up to a checkpoint interval (default
///     60 minutes) away. Measured on the live bank: the jobs finished at 20:14 and the backlog had
///     not moved by 20:39.
///     <para>
///         PendingEmbedJob (.NET-F1) fixed this by replacing the bespoke pre/post-job sweep with
///         plain job-list ordering: registered LAST (AppRegistrations), its HasWorkAsync sees
///         whatever an earlier job in the same MaintenanceJobRunner.RunDueAsync pass just committed,
///         on the same connection — no CreatedWork flag or second sweep required. This test proves
///         that property survives: a fake earlier job creates a real pending row, and PendingEmbedJob
///         signals the embed topic for it before RunOnceAsync returns.
///     </para>
///     <para>
///         WP11-B2: PendingEmbedJob no longer embeds inline, so "in the same pass" now means
///         "enqueued in the same pass" — the actual pending-&gt;embedded transition is
///         EmbedDrainService's own (EmbedDrainServiceTests), asynchronous by design (the whole point
///         of B2 is that the drain no longer runs synchronously inside a maintenance pass).
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedSweepAfterJobsTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-after-jobs");
    private readonly SqliteConnectionFactory _factory;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly BackgroundTelemetryProbe _probe = new(BankMaintenanceHostedService.OperationName);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    public EmbedSweepAfterJobsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose()
    {
        _probe.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     The pass must signal a drain for a row a job creates, in the SAME pass — asserted by a
    ///     fake job that only then inserts a real pending row, ordered before PendingEmbedJob in the
    ///     list.
    /// </summary>
    [Fact]
    public async Task APassWhoseJobCreatesPendingRows_SignalsTheDrainInTheSamePass()
    {
        await ConfigureProviderAsync();
        var pendingCreatingJob = new PendingCreatingJob(ProjectId);
        var pump = TestData.NewEmbedDrainPump();
        var pendingEmbedJob = new PendingEmbedJob(new EntryEmbedder(new CountingEmbeddingService(), _modelMigrationLease, _time), pump);

        await ServiceWith(pendingCreatingJob, pendingEmbedJob).RunOnceAsync(TestContext.Current.CancellationToken);

        (await ReadEmbedStatesAsync()).ShouldAllBe(state => state == "pending",
            "WP11-B2: the row stays pending — the pass only signals the drain, it no longer embeds inline");
        pump.EnqueuedCount.ShouldBe(1, "the pending-creating job's own output must be signalled in the pass that created it, not the next one");
    }

    private async Task ConfigureProviderAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<IReadOnlyList<string>> ReadEmbedStatesAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var states = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM entries WHERE project_id = @projectId", new { projectId = ProjectId },
            cancellationToken: TestContext.Current.CancellationToken));
        return [.. states];
    }

    private BankMaintenanceHostedService ServiceWith(params IMaintenanceJob[] jobs) =>
        new(_factory, _time, _probe.Telemetry, new FakeLogger<BankMaintenanceHostedService>(),
            NoOpNoiseEntryStore.Instance, new SqlitePromotionQueueStore(_factory, _time),
            new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance),
            new MaintenanceJobRunner(_time, new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance), jobs);

    /// <summary>Stands in for chunk-backfill: writes a real pending row directly, the same shape a chunker's INSERT leaves.</summary>
    private sealed class PendingCreatingJob(string projectId) : IMaintenanceJob
    {
        public string Name => "pending-creating";

        public string DisplayName => "create pending rows";

        public TimeSpan? Interval => null;

        public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', @projectId, 0, 0)
                """,
                new { hash = Guid.NewGuid().ToString("N"), path = "backfilled.md", value = "backfilled content", projectId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
    }
}
