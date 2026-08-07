using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Hosted-service lifecycle contract: a startup pass runs one checkpoint before
///     traffic, the periodic timer re-reads its interval after every tick, settings
///     failures fall back to defaults without killing the loop, and StopAsync runs a
///     final best-effort checkpoint.
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
        _service = new BankMaintenanceHostedService(_factory, _time, _logger);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private string WalPath => Path.Combine(_dataRoot, "memory.db-wal");

    /// <summary>Waits until the logger observed the event at least the given times (deterministic sync point).</summary>
    private async Task WaitForLogAsync(int eventId, int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_logger.Collector.GetSnapshot().Count(r => r.Id.Id == eventId) >= count)
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException($"EventId {eventId} was not observed {count} time(s) within 5s");
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

        // The startup pass's checkpoint (510) is the sync point: only then write churn
        // the ticks must reap — a fixed delay would race the bank create + schema ensure.
        await WaitForLogAsync(510, 1, TestContext.Current.CancellationToken);
        await InsertSettingAsync("probe.x", "1", TestContext.Current.CancellationToken);
        var walAfterStartup = new FileInfo(WalPath).Length;
        walAfterStartup.ShouldBeGreaterThan(0);
        var baseline = _logger.Collector.GetSnapshot().Count(r => r.Id.Id == 510);

        _time.Advance(TimeSpan.FromMinutes(1)); // configured interval
        await WaitForLogAsync(510, baseline + 1, TestContext.Current.CancellationToken);

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

        await WaitForLogAsync(510, 1, TestContext.Current.CancellationToken);
        var baseline = _logger.Collector.GetSnapshot().Count(r => r.Id.Id == 510);

        // Widen the interval before the first tick: the re-read after that tick (the
        // timer period is re-read after every run) must defer the next one.
        await InsertSettingAsync("maintenance.checkpoint-interval-minutes.global", "1440",
            TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(1)); // fires the tick scheduled at the old 1-minute period
        await WaitForLogAsync(510, baseline + 1, TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(1)); // new 1440-minute period: no tick
        await Task.Delay(100, TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();
        _logger.Collector.GetSnapshot().Count(r => r.Id.Id == 510).ShouldBe(baseline + 1);

        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_SettingsTableMissing_LoopSurvivesWithDefaults()
    {
        // A plain DROP is undone by EnsureAsync's CREATE TABLE IF NOT EXISTS on every
        // bank open, so the broken channel is simulated with a view of that name over
        // a missing table: the service's SELECT then fails on every open.
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

        // Startup pass completes, then the pre-timer interval read fails → 514.
        await WaitForLogAsync(514, 1, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(60)); // default interval
        await WaitForLogAsync(514, 2, TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();

        await _service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var run = _service.StartAsync(cts.Token);

        await WaitForLogAsync(510, 1, TestContext.Current.CancellationToken);

        await _service.StopAsync(TestContext.Current.CancellationToken);

        run.IsCompletedSuccessfully.ShouldBeTrue();
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 515);
    }
}
