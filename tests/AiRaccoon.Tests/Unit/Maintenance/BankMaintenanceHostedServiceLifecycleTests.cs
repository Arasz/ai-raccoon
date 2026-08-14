using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Watch;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Lifecycle contract: a startup pass checkpoints once before traffic, the periodic timer
///     re-reads its interval every tick, settings failures fall back to defaults without killing
///     the loop, and StopAsync runs a final best-effort checkpoint — all signal-driven (TickSignal), no polling sleeps.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankMaintenanceHostedServiceLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("bank-maintenance");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time;
    private readonly FakeLogger<BankMaintenanceHostedService> _logger;
    private readonly BankMaintenanceHostedService _service;

    public BankMaintenanceHostedServiceLifecycleTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _logger = new FakeLogger<BankMaintenanceHostedService>();
        _service = new BankMaintenanceHostedService(_factory, _time, TestTelemetry.None, _logger, new FakeMemoryStore());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private string WalPath => Path.Combine(_dataRoot, "memory.db-wal");

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(150);

    private Task<bool> WaitForTicksAsync(long target) =>
        _service.Ticks.WaitAsync(target, SignalTimeout, TestContext.Current.CancellationToken);

    /// <summary>
    ///     Advances the fake clock in steps until the tick signal fires: the periodic
    ///     timer arms only after the startup pass, so a single Advance can race it.
    /// </summary>
    private async Task AdvanceUntilTicksAsync(long target, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            _time.Advance(step);
            if (await _service.Ticks.WaitAsync(target, AbsenceWindow, TestContext.Current.CancellationToken))
            {
                return;
            }
        }

        throw new TimeoutException($"Tick {target} did not fire within {SignalTimeout}");
    }

    private async Task InsertSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_CheckpointRunsAtConfiguredInterval()
    {
        await InsertSettingAsync("maintenance.checkpoint-interval-minutes.global", "1",
            TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        // Startup-pass completion is the sync point, so no fixed delay races the bank create + schema ensure.
        (await WaitForTicksAsync(1)).ShouldBeTrue();
        await InsertSettingAsync("probe.x", "1", TestContext.Current.CancellationToken);
        var walAfterStartup = new FileInfo(WalPath).Length;
        walAfterStartup.ShouldBeGreaterThan(0);

        await AdvanceUntilTicksAsync(2, TimeSpan.FromMinutes(1)); // configured interval

        run.IsFaulted.ShouldBeFalse();
        new FileInfo(WalPath).Length.ShouldBe(0);

        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_IntervalReReadPerTick()
    {
        await InsertSettingAsync("maintenance.checkpoint-interval-minutes.global", "1",
            TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        (await WaitForTicksAsync(1)).ShouldBeTrue(); // startup pass
        var baseline = _service.Ticks.Count;

        // Waits for the re-read signal before widening the setting, so the NEXT tick is the
        // one that re-reads and defers.
        await AdvanceUntilTicksAsync(baseline + 1, TimeSpan.FromMinutes(1));
        (await _service.IntervalReReads.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await InsertSettingAsync("maintenance.checkpoint-interval-minutes.global", "1440",
            TestContext.Current.CancellationToken);

        await AdvanceUntilTicksAsync(baseline + 2, TimeSpan.FromMinutes(1)); // old 1-minute period
        (await _service.IntervalReReads.WaitAsync(2, SignalTimeout, TestContext.Current.CancellationToken))
            .ShouldBeTrue(); // period re-read as 1440

        _time.Advance(TimeSpan.FromMinutes(1)); // re-read period is 1440: no tick
        (await _service.Ticks.WaitAsync(baseline + 3, AbsenceWindow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        run.IsFaulted.ShouldBeFalse();
        _service.Ticks.Count.ShouldBe(baseline + 2);

        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_SettingsTableMissing_LoopSurvivesWithDefaults()
    {
        // A plain DROP is undone by EnsureAsync's CREATE TABLE IF NOT EXISTS on every open, so this
        // simulates failure with a view over a missing table instead — the service's SELECT then fails.
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE settings";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.CommandText = "CREATE VIEW settings AS SELECT 'x' AS key, 'y' AS value FROM missing_table";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        (await WaitForTicksAsync(1)).ShouldBeTrue(); // startup pass completes
        (await _service.TimerArmed.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        await AdvanceUntilTicksAsync(2, TimeSpan.FromMinutes(60)); // default interval

        run.IsFaulted.ShouldBeFalse();
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 514);

        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        (await WaitForTicksAsync(1)).ShouldBeTrue();

        await _service.StopAsync(TestContext.Current.CancellationToken);

        run.IsCompletedSuccessfully.ShouldBeTrue();
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 515);
    }
}
