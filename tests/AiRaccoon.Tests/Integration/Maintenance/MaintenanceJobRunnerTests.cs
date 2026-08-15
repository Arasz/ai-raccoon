using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        await new MaintenanceJobRunner(_time, NullLogger<MaintenanceJobRunner>.Instance).RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

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

    private MaintenanceJobRunner Runner() => new(_time, NullLogger<MaintenanceJobRunner>.Instance);

    private async Task<SqliteConnection> OpenAsync() =>
        await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

    private sealed class CountingJob(string name, TimeSpan? interval) : IMaintenanceJob
    {
        public int Runs { get; private set; }

        public bool ThrowOnce { get; init; }

        public bool ThrowAlways { get; init; }

        private bool _thrown;

        public string Name => name;

        public string DisplayName => $"counting job {name}";

        public TimeSpan? Interval => interval;

        public Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            if (ThrowAlways || (ThrowOnce && !_thrown))
            {
                _thrown = true;
                throw new InvalidOperationException("job failed");
            }

            Runs++;
            return Task.FromResult(false);
        }
    }
}
