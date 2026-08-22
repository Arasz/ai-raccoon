using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     The round-trip gate: a value the CLI writes must be visible to the actual consuming
///     subsystem, not merely present as a settings row (a wrong settings key would pass a
///     write-only assertion but leave the reader on its default forever). Each test writes
///     through <see cref="PerformanceCommands" /> and then runs the real reader
///     (<see cref="MetricsFlusher" /> or <see cref="MetricsRetentionJob" />) against the same
///     underlying settings store — break it by writing to a different key and the reader keeps
///     using its default, failing the assertion.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PerformanceCommandsRoundTripTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("performance-cli-roundtrip");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>Forwards IMemoryStore's settings surface to a given ISettingsStore so a CLI write
    /// and a reader's own read go through the exact same backing store.</summary>
    private sealed class DelegatingMemoryStore(ISettingsStore inner) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetSettingAsync(key, cancellationToken);

        public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
            inner.SetSettingAsync(key, value, cancellationToken);
    }

    private sealed class NoopMetricsStore : IMetricsStore
    {
        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>MetricsFlusher.ApplyBufferCapacitySafeAsync reads buffer capacity exactly once, at
    /// the top of ExecuteAsync — "next server restart" in product terms.</summary>
    [Fact]
    public async Task SetBufferCapacity_IsAppliedByMetricsFlusher_OnStartup()
    {
        var settings = new InMemorySettings();
        var (exit, _, _) = await CliRun.RunAsync(["settings", "performance", "buffer-capacity", "17"],
            TestData.CreateConfigCommands(new DelegatingMemoryStore(settings), performance: new PerformanceCommands()));
        exit.ShouldBe(0);

        var buffer = new MeasurementBuffer(MetricsConfigKeys.DefaultBufferCapacity);
        var time = new FakeTimeProvider(FixedNow);
        var flusher = new MetricsFlusher(buffer, new NoopMetricsStore(), settings, time, TestTelemetry.None,
            NullLogger<MetricsFlusher>.Instance);

        using var cts = new CancellationTokenSource();
        var run = flusher.StartAsync(cts.Token);
        await flusher.TimerArmed.WaitAsync(1, TestContext.Current.CancellationToken);

        buffer.Capacity.ShouldBe(17, "MetricsFlusher must have read the CLI-set capacity at startup, not the default");
        run.IsFaulted.ShouldBeFalse();
        await flusher.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>MetricsFlusher.ReadFlushIntervalSafeAsync re-reads the interval inside the periodic
    /// loop — "next flush tick" in product terms.</summary>
    [Fact]
    public async Task SetFlushInterval_IsAppliedByMetricsFlusher_OnTheNextTick()
    {
        var settings = new InMemorySettings();
        var (exit, _, _) = await CliRun.RunAsync(["settings", "performance", "flush-interval", "5"],
            TestData.CreateConfigCommands(new DelegatingMemoryStore(settings), performance: new PerformanceCommands()));
        exit.ShouldBe(0);

        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(new Measurement("test.metric", MeasurementKind.Counter, 1, "count", FixedNow));
        var store = new RecordingMetricsStore();
        var time = new FakeTimeProvider(FixedNow);
        var flusher = new MetricsFlusher(buffer, store, settings, time, TestTelemetry.None,
            NullLogger<MetricsFlusher>.Instance);

        using var cts = new CancellationTokenSource();
        var run = flusher.StartAsync(cts.Token);
        await flusher.TimerArmed.WaitAsync(1, TestContext.Current.CancellationToken);

        // The flusher's own tick, on fake time: advance once past the interval, then wait on the
        // flush signal itself — no wall-clock verdict (PR #464).
        time.Advance(TimeSpan.FromSeconds(5));
        await flusher.Flushes.WaitAsync(1, TestContext.Current.CancellationToken);

        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms",
            "the flusher must have ticked at the CLI-set 5s interval, not the 30s default");
        run.IsFaulted.ShouldBeFalse();
        await flusher.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class RecordingMetricsStore : IMetricsStore
    {
        public List<Measurement> Saved { get; } = [];

        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default)
        {
            Saved.AddRange(measurements);
            return Task.CompletedTask;
        }
    }

    /// <summary>MetricsRetentionJob re-reads the retention window on every maintenance pass —
    /// "next maintenance pass" in product terms. Reads through the real settings table, not a fake.</summary>
    [Fact]
    public async Task SetRetention_IsAppliedByMetricsRetentionJob_OnTheNextPass()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = new DelegatingMemoryStore(new SqliteSettingsStore(factory));

        var (exit, _, _) = await CliRun.RunAsync(["settings", "performance", "retention", "5"],
            TestData.CreateConfigCommands(store, performance: new PerformanceCommands()));
        exit.ShouldBe(0);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedMetricAsync(connection, "outside-5-day-window", FixedNow.AddDays(-6).ToUnixTimeSeconds());
        await SeedMetricAsync(connection, "inside-5-day-window", FixedNow.AddDays(-4).ToUnixTimeSeconds());

        var time = new FakeTimeProvider(FixedNow);
        await new MaintenanceJobRunner(time, NullLogger<MaintenanceJobRunner>.Instance)
            .RunDueAsync(connection, [new MetricsRetentionJob(time)], TestContext.Current.CancellationToken);

        var survivors = (await connection.QueryAsync<string>("SELECT name FROM metrics ORDER BY name")).ToList();
        survivors.ShouldBe(["inside-5-day-window"],
            "the reaper must have purged at the CLI-set 5-day window, not the 28-day default");
    }

    private static async Task SeedMetricAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string name, long recordedAt) =>
        await connection.ExecuteAsync(
            "INSERT INTO metrics (name, kind, value, unit, recorded_at) VALUES (@name, 'counter', 1, 'ms', @recordedAt)",
            new { name, recordedAt });
}
