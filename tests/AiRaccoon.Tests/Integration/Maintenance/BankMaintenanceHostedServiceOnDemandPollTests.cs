using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ADR-0076: the on-demand poll runs on its own short cadence
///     (<see cref="BankMaintenanceHostedService.OnDemandPollInterval" />, 15s), independent of the
///     heavy checkpoint/vacuum/purge pass's own (long, configurable) cadence. Two things this must
///     be true of at once, which is exactly the regression the owner asked to see pinned: an
///     on-demand job is picked up within the poll interval, and a cadence-based job's own interval
///     is completely unaffected by how often the poll now wakes.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankMaintenanceHostedServiceOnDemandPollTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    /// <summary>Step budget for the advance-until helpers: a count of fake-clock steps, never a wall-clock deadline (PR #464).</summary>
    private const int MaxAdvanceSteps = 200;
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(150);

    private readonly string _dataRoot = TestData.CreateTempRoot("bank-maintenance-on-demand");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time;
    private readonly OnDemandJob _onDemandJob = new();
    private readonly CadenceJob _hourlyJob = new(TimeSpan.FromHours(1));
    private readonly BankMaintenanceHostedService _service;

    public BankMaintenanceHostedServiceOnDemandPollTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _service = new BankMaintenanceHostedService(_factory, _time, TestTelemetry.None,
            new FakeLogger<BankMaintenanceHostedService>(), NoOpNoiseEntryStore.Instance,
            new SqlitePromotionQueueStore(_factory, _time),
            new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance),
            new MaintenanceJobRunner(_time, new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance),
            [_onDemandJob, _hourlyJob]);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private async Task AdvanceUntilOnDemandPollsAsync(long target)
    {
        for (var step = 0; step < MaxAdvanceSteps; step++)
        {
            _time.Advance(BankMaintenanceHostedService.OnDemandPollInterval);
            if (await _service.OnDemandPolls.WaitAsync(target, AbsenceWindow, TestContext.Current.CancellationToken))
            {
                return;
            }
        }

        throw new TimeoutException($"on-demand poll {target} did not fire within {MaxAdvanceSteps} fake-clock steps");
    }

    [RetryFact]
    public async Task AnOnDemandJob_IsPickedUp_WithinThePollInterval_LongBeforeTheHourlyCheckpointCadence()
    {
        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        await _service.Ticks.WaitAsync(1, TestContext.Current.CancellationToken); // startup pass
        _onDemandJob.Runs.ShouldBe(1); // the startup pass itself always calls RunDueAsync (crash-recovery guarantee)

        _onDemandJob.SimulateNewWorkArriving();
        var baseline = _service.OnDemandPolls.Count;
        await AdvanceUntilOnDemandPollsAsync(baseline + 1);

        _onDemandJob.Runs.ShouldBe(2); // picked up on the very next 15s poll, not after an hour

        run.IsFaulted.ShouldBeFalse();
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The regression this refactor could introduce: the on-demand poll must not make an hourly
    ///     job run more often just because the loop now wakes every 15 seconds instead of every hour.
    /// </summary>
    [RetryFact]
    public async Task AnHourlyCadenceJob_StillRunsOnlyOncePerHour_DespiteThePollWakingEvery15Seconds()
    {
        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        await _service.Ticks.WaitAsync(1, TestContext.Current.CancellationToken); // startup pass
        _hourlyJob.Runs.ShouldBe(1); // due on first-ever pass (lastRunAt is null), same as any Interval-gated job

        // 40 on-demand polls at 15s = 10 minutes: nowhere near the hourly job's own interval.
        for (var i = 0; i < 40; i++)
        {
            _time.Advance(BankMaintenanceHostedService.OnDemandPollInterval);
        }

        await Task.Delay(AbsenceWindow, TestContext.Current.CancellationToken); // let the polls actually happen
        _hourlyJob.Runs.ShouldBe(1); // unchanged: still gated by its own Interval, not by poll frequency

        run.IsFaulted.ShouldBeFalse();
        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class OnDemandJob : IMaintenanceJob
    {
        private bool _hasWork = true; // due on the very first pass, same as any never-run job

        public int Runs { get; private set; }

        public string Name => "on-demand-test-job";

        public string DisplayName => "on-demand test job";

        public TimeSpan? Interval => null;

        public void SimulateNewWorkArriving() => _hasWork = true;

        public ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_hasWork);

        public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            Runs++;
            _hasWork = false; // drained until SimulateNewWorkArriving is called again
            return ValueTask.FromResult(false);
        }
    }

    private sealed class CadenceJob(TimeSpan interval) : IMaintenanceJob
    {
        public int Runs { get; private set; }

        public string Name => "cadence-test-job";

        public string DisplayName => "cadence test job";

        public TimeSpan? Interval => interval;

        public ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(false);
        }
    }
}
