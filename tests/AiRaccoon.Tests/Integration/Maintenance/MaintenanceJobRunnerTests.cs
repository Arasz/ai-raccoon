using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ADR-0070: the maintenance cadence clock moves from the process into the bank. The clock it
///     replaces was a field seeded on first run, so a bank only ever opened by short-lived processes
///     never reached any interval — measured on a real 183 MB bank, 42 MB of free pages that no
///     VACUUM would ever have collected.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MaintenanceJobRunnerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("maintenance-jobs");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(Start);

    public MaintenanceJobRunnerTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     E8 (WP11-B2): two overlapping RunDueAsync passes — the heavy pass and the 15s on-demand
    ///     poll both call this, on two different connections — used to both SELECT and embed the
    ///     same rows (the duplicate-inference bug this WP closes structurally). Now both enqueue;
    ///     the second enqueue coalesces away, so the single-reader EmbedDrainService consumer needs
    ///     exactly one drain pass to embed every row.
    /// </summary>
    [Fact]
    public async Task TwoOverlappingPasses_EnqueueOneDrain_AndEachRowIsEmbeddedOnce()
    {
        var pump = TestData.NewEmbedDrainPump();
        await SeedProviderAndPendingRowsAsync(5);
        var entryEmbedder = new EntryEmbedder(new CountingEmbeddingService(),
            Substitute.For<IModelMigrationLease>(), _time);
        var job = new PendingEmbedJob(entryEmbedder, pump);

        await using (var connection1 = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await Runner().RunDueAsync(connection1, [job], TestContext.Current.CancellationToken);
        }

        await using (var connection2 = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await Runner().RunDueAsync(connection2, [job], TestContext.Current.CancellationToken);
        }

        pump.EnqueuedCount.ShouldBe(1, "the second pass's signal must coalesce, not queue a second drain");
        pump.CoalescedCount.ShouldBe(1);

        var request = pump.DrainUpTo(1).ShouldHaveSingleItem();
        var drainService = new EmbedDrainService(pump, _factory, entryEmbedder, new FakeCodeEmbedder(),
            new SqliteSettingsStore(_factory), TestTelemetry.None, NullLogger<EmbedDrainService>.Instance);
        await drainService.DrainOnceAsync(request, TestContext.Current.CancellationToken);

        (await PendingCountAsync()).ShouldBe(0, "one drain pass is enough — every row is embedded exactly once");
    }

    private async Task SeedProviderAndPendingRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                """,
                new { hash = $"h{i}", path = $"p{i}.md", value = $"pending row {i}" },
                cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    private async Task<long> PendingCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE embed_state = 'pending'",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AJobThatHasNeverRun_RunsOnTheFirstPass()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("first", TimeSpan.FromHours(2));

        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        job.Runs.ShouldBe(1);
    }

    /// <summary>
    ///     The defect this whole change exists for: a new process must not reset the clock. The old
    ///     implementation seeded its timer in memory, so every restart began the interval again.
    /// </summary>
    [Fact]
    public async Task ANewRunner_DoesNotRestartTheInterval()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("persisted", TimeSpan.FromHours(2));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(30));
        // A brand-new runner is a brand-new process. It reads the bank, not its own memory.
        await new MaintenanceJobRunner(_time, new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        job.Runs.ShouldBe(1, "30 minutes into a 2-hour interval, a restart must not earn another run");
    }

    [Fact]
    public async Task AJob_RunsAgainOnceItsIntervalHasPassed()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("periodic", TimeSpan.FromHours(2));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromHours(2));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        job.Runs.ShouldBe(2);
    }

    /// <summary>A null interval means once per bank, ever — however long the bank lives.</summary>
    [Fact]
    public async Task ARunOnceJob_NeverRunsTwice()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("once", interval: null);

        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromDays(365));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        job.Runs.ShouldBe(1);
    }

    /// <summary>
    ///     A failed job must not be stamped, or a transient lock would retire a run-once job forever
    ///     without it ever having done its work.
    /// </summary>
    [Fact]
    public async Task AFailedJob_IsNotStamped_AndRetriesNextPass()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("flaky", interval: null) { ThrowOnce = true };

        var first = await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        var second = await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        first.Single().Ran.ShouldBeFalse();
        first.Single().Error.ShouldNotBeNull();
        second.Single().Ran.ShouldBeTrue("an unstamped job must be retried, not retired");
        job.Runs.ShouldBe(1);
    }

    /// <summary>One job's failure must not stop the ones after it in the list.</summary>
    [Fact]
    public async Task AFailingJob_DoesNotBlockTheRest()
    {
        await using var connection = await OpenAsync();
        var failing = new CountingJob("bad", interval: null) { ThrowAlways = true };
        var healthy = new CountingJob("good", interval: null);

        await Runner().RunDueAsync(connection, [failing, healthy], TestContext.Current.CancellationToken);

        healthy.Runs.ShouldBe(1);
    }

    /// <summary>D6: a broken due-ness check must not stop the pass — the SELECT and
    /// HasWorkAsync happen before the run try/catch, so either escaping stops every later job.</summary>
    [Fact]
    public async Task AJobWhoseHasWorkAsyncThrows_DoesNotStopLaterJobs()
    {
        await using var connection = await OpenAsync();
        var broken = new CountingJob("broken-check", interval: null) { ThrowFromHasWorkAsync = true };
        var healthy = new CountingJob("good-after", interval: null);

        var outcomes = await Runner().RunDueAsync(connection, [broken, healthy], TestContext.Current.CancellationToken);

        outcomes[0].Ran.ShouldBeFalse();
        outcomes[0].Error.ShouldNotBeNull();
        healthy.Runs.ShouldBe(1, "a job registered after a broken HasWorkAsync check must still run in the same pass");
    }

    [Fact]
    public async Task AJobWhoseHasWorkAsyncThrows_LogsTheSkipWithJobName()
    {
        await using var connection = await OpenAsync();
        var logger = new FakeLogger<MaintenanceJobRunner>();
        var broken = new CountingJob("broken-check", interval: null) { ThrowFromHasWorkAsync = true };

        await new MaintenanceJobRunner(_time, new NoOpMeasurementRecorder(), logger).RunDueAsync(connection, [broken], TestContext.Current.CancellationToken);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Id.Id.ShouldBe(527);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain("broken-check");
    }

    /// <summary>D6: the lastRun ledger SELECT shares the same guard, so a broken read is
    /// skipped and logged rather than escaping RunDueAsync.</summary>
    [Fact]
    public async Task AJobWhoseLastRunReadThrows_IsSkippedAndLoggedInsteadOfEscaping()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("DROP TABLE maintenance_jobs");
        var job = new CountingJob("no-ledger", interval: null);

        var outcomes = await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        outcomes.Single().Ran.ShouldBeFalse();
        outcomes.Single().Error.ShouldNotBeNull();
        job.Runs.ShouldBe(0);
    }

    /// <summary>D6 follow-up: VacuumJob.RefreshIntervalAsync reads its interval from the same
    /// connection, before the ledger SELECT — a broken read there must share the same guard.</summary>
    [Fact]
    public async Task AVacuumJobWhoseIntervalReadThrows_DoesNotStopLaterJobs()
    {
        await using var connection = await OpenAsync();
        var brokenVacuum = new VacuumJob((_, _) => throw new InvalidOperationException("settings read failed"));
        var healthy = new CountingJob("good-after-vacuum", interval: null);

        var outcomes = await Runner().RunDueAsync(connection, [brokenVacuum, healthy], TestContext.Current.CancellationToken);

        outcomes[0].Ran.ShouldBeFalse();
        outcomes[0].Error.ShouldNotBeNull();
        healthy.Runs.ShouldBe(1, "a job registered after a broken vacuum interval read must still run in the same pass");
    }

    /// <summary>D6 follow-up: shutdown cancellation must still abort the pass, not be logged as a
    /// benign per-job skip — the `when (ex is not OperationCanceledException)` filter is the only
    /// thing standing between those two behaviours.</summary>
    [Fact]
    public async Task AJobWhoseHasWorkAsyncThrowsOperationCanceled_PropagatesOutOfRunDueAsync()
    {
        await using var connection = await OpenAsync();
        var cancelling = new CountingJob("cancelling", interval: null) { ThrowOperationCanceledFromHasWorkAsync = true };

        await Should.ThrowAsync<OperationCanceledException>(
            () => Runner().RunDueAsync(connection, [cancelling], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARunRecordsItsTimestampAndCount()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("counted", TimeSpan.FromHours(2));

        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(2));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        var row = await connection.QuerySingleAsync<(long LastRunAt, long RunCount)>(
            "SELECT last_run_at AS LastRunAt, run_count AS RunCount FROM maintenance_jobs WHERE name = 'counted'");
        row.RunCount.ShouldBe(2);
        row.LastRunAt.ShouldBe(Start.AddHours(2).ToUnixTimeSeconds());
    }

    /// <summary>
    ///     WP3 (#477): a completed run records `job.&lt;name&gt;.duration_ms`; the duration the runner
    ///     already computes at :71/:94 for the log line goes to the measurement recorder too, under
    ///     the self-metrics project id so `memory_performance` can find it (route (a) — no change to
    ///     <see cref="IMaintenanceJob" />).
    /// </summary>
    [Fact]
    public async Task ACompletedRun_RecordsJobDurationMetric()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("duration-job", interval: null);
        var recorder = new RecordingMeasurementRecorder();

        await Runner(recorder).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        var measurement = recorder.Recorded.ShouldHaveSingleItem();
        measurement.Name.ShouldBe("job.duration-job.duration_ms");
        measurement.Kind.ShouldBe(MeasurementKind.Histogram);
        measurement.Unit.ShouldBe("ms");
        measurement.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
    }

    /// <summary>
    ///     WP3 (#477): a job that opts into <see cref="IReportsOutstandingRows" /> also gets its
    ///     `job.&lt;name&gt;.rows` gauge recorded — route (a)'s escape hatch for the "rows" half of
    ///     the issue, reachable without touching <see cref="IMaintenanceJob" /> or any of its ten
    ///     implementations (none opt in today).
    /// </summary>
    [Fact]
    public async Task AJobWithOutstandingRows_RecordsJobRowsMetric()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJobWithOutstandingRows("rows-job", outstandingRows: 7);
        var recorder = new RecordingMeasurementRecorder();

        await Runner(recorder).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        var rows = recorder.Recorded.Single(m => m.Name == "job.rows-job.rows");
        rows.Kind.ShouldBe(MeasurementKind.Gauge);
        rows.Value.ShouldBe(7);
        rows.Unit.ShouldBe("count");
        rows.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
    }

    /// <summary>WP3 (#477): a job that is not due neither ran nor produced anything to measure — no metric at all, not a zero.</summary>
    [Fact]
    public async Task ANotDueJob_RecordsNeitherMetric()
    {
        await using var connection = await OpenAsync();
        var job = new CountingJob("not-due", TimeSpan.FromHours(2));
        var recorder = new RecordingMeasurementRecorder();
        await Runner(recorder).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        recorder.Recorded.Clear();

        await Runner(recorder).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        recorder.Recorded.ShouldBeEmpty();
    }

    private MaintenanceJobRunner Runner(IMeasurementRecorder? measurements = null) =>
        new(_time, measurements ?? new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance);

    private async Task<SqliteConnection> OpenAsync() =>
        await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

    private sealed class CountingJob(string name, TimeSpan? interval) : IMaintenanceJob
    {
        public int Runs { get; private set; }

        public bool ThrowOnce { get; init; }

        public bool ThrowAlways { get; init; }

        public bool ThrowFromHasWorkAsync { get; init; }

        public bool ThrowOperationCanceledFromHasWorkAsync { get; init; }

        private bool _thrown;

        public string Name => name;

        public string DisplayName => $"counting job {name}";

        public TimeSpan? Interval => interval;

        public ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            if (ThrowOperationCanceledFromHasWorkAsync)
            {
                throw new OperationCanceledException("shutting down");
            }

            return ThrowFromHasWorkAsync
                ? throw new InvalidOperationException("has-work check failed")
                : ValueTask.FromResult(false);
        }

        public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            if (ThrowAlways || (ThrowOnce && !_thrown))
            {
                _thrown = true;
                throw new InvalidOperationException("job failed");
            }

            Runs++;
            return ValueTask.FromResult(false);
        }
    }

    /// <summary>WP3 (#477) fixture: a job that opts into <see cref="IReportsOutstandingRows" />. None of the ten real jobs do this today — route (a)'s point is that this is possible without touching <see cref="IMaintenanceJob" />.</summary>
    private sealed class CountingJobWithOutstandingRows(string name, long outstandingRows)
        : IMaintenanceJob, IReportsOutstandingRows
    {
        public string Name => name;

        public string DisplayName => $"counting job {name} with outstanding rows";

        public TimeSpan? Interval => null;

        public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<long> CountOutstandingRowsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(outstandingRows);
    }
}
