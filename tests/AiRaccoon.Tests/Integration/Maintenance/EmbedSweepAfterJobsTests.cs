using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Observability;
using AiRaccoon.Tests.Unit.Watch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     `chunk-backfill` writes its pieces unembedded, and the pass's embed sweep runs BEFORE the
///     jobs — so on a real bank the backfill's 13,578 new rows sat pending, waiting for the next
///     pass up to a checkpoint interval (default 60 minutes) away. Measured on the live bank: the
///     jobs finished at 20:14 and the backlog had not moved by 20:39.
///     <para>
///         Nothing was broken; the rows drain eventually. But every user installing this version
///         pays that window, during which the rows are absent from vector search. A job that creates
///         work should have that work swept in the same pass that created it.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedSweepAfterJobsTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-after-jobs");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeWatchMemoryStore _memory = new();
    private readonly BackgroundTelemetryProbe _probe = new(BankMaintenanceHostedService.OperationName);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    public EmbedSweepAfterJobsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _memory.ProjectIds.Add(ProjectId);
    }

    public void Dispose()
    {
        _probe.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     The pass must sweep again after a job runs. Asserted by a job that only then makes rows
    ///     pending — so a sweep that happens only before the jobs sees nothing and never embeds.
    /// </summary>
    [Fact]
    public async Task APassWhoseJobCreatesPendingRows_EmbedsThemInTheSamePass()
    {
        var job = new PendingCreatingJob(_memory, ProjectId);

        await ServiceWith(job).RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.ShouldContain(ProjectId,
            "the backfill's own output must be swept in the pass that created it, not the next one");
    }

    /// <summary>A pass where no job ran must not pay for a second sweep it has no reason to make.</summary>
    [Fact]
    public async Task APassWhereNoJobRan_SweepsOnce()
    {
        _memory.PendingCounts[ProjectId] = 3;

        await ServiceWith().RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.Count(id => id == ProjectId).ShouldBe(1);
    }

    /// <summary>
    ///     The gate. A job that ran but created nothing — vacuum, reclaim — must not earn a second
    ///     sweep: it doubles the window in which a background embed races an in-flight write, which
    ///     is what turned `Embeddings_ConfigureOpenAi_RoutesThroughTheConfiguredEndpoint` red on CI
    ///     while passing locally.
    /// </summary>
    [Fact]
    public async Task APassWhoseJobCreatedNothing_DoesNotSweepTwice()
    {
        _memory.PendingCounts[ProjectId] = 3;

        await ServiceWith(new NoWorkJob()).RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.Count(id => id == ProjectId).ShouldBe(1,
            "a job that made no work must not trigger a second sweep");
    }

    private BankMaintenanceHostedService ServiceWith(params IMaintenanceJob[] jobs) =>
        new(_factory, _time, _probe.Telemetry, new FakeLogger<BankMaintenanceHostedService>(), _memory,
            NoOpNoiseEntryStore.Instance, new SqlitePromotionQueueStore(_factory, _time),
            new SqliteSearchQualityService(_factory),
            new MaintenanceJobRunner(_time, NullLogger<MaintenanceJobRunner>.Instance), jobs);

    /// <summary>Stands in for vacuum and reclaim: runs, changes nothing that needs embedding.</summary>
    private sealed class NoWorkJob : IMaintenanceJob
    {
        public string Name => "no-work";

        public string DisplayName => "do nothing that needs embedding";

        public TimeSpan? Interval => null;

        public Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>Stands in for chunk-backfill: makes rows pending only when it runs.</summary>
    private sealed class PendingCreatingJob(FakeWatchMemoryStore memory, string projectId) : IMaintenanceJob
    {
        public string Name => "pending-creating";

        public string DisplayName => "create pending rows";

        public TimeSpan? Interval => null;

        public Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            memory.PendingCounts[projectId] = 42;
            return Task.FromResult(true);
        }
    }
}
