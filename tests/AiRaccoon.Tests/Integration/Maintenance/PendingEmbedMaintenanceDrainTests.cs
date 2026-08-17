using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     The acceptance gate for .NET-F1: `repair reingest --apply` (and any other writer) leaves
///     rows `embed_state = 'pending'`, and nothing drained them until the heavy pass's own
///     (default 60-minute) cadence — measured on the real bank, 14,941 rows sat unembedded for
///     hours. This drives the real <see cref="BankMaintenanceHostedService" /> loop, not just
///     <see cref="PendingEmbedJob" /> in isolation, so it also proves the job is actually wired into
///     the maintenance machinery, not merely capable of draining if someone remembered to call it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PendingEmbedMaintenanceDrainTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot = TestData.CreateTempRoot("pending-embed-drain");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time;

    public PendingEmbedMaintenanceDrainTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>The fix: with PendingEmbedJob registered, the loop's own startup pass — no restart, no 15s wait even — drains everything.</summary>
    [Fact]
    public async Task StartupPass_WithPendingEmbedJobRegistered_DrainsPendingRowsWithinTheStartupPass()
    {
        await SeedPendingRowsAsync(3);
        await ConfigureProviderAsync();
        var jobs = new IMaintenanceJob[] { new PendingEmbedJob(new EntryEmbedder(new CountingEmbeddingService())) };
        var service = ServiceWith(jobs);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        (await service.Ticks.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await PendingCountAsync()).ShouldBe(0);

        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The red baseline (prove-the-check-fails): the maintenance loop with no embed-draining job
    ///     registered — the exact shape before PendingEmbedJob existed, when the only sweep was the
    ///     heavy pass's own (default 60-minute) cadence — leaves the same seeded rows pending through
    ///     the startup pass and five on-demand polls. Confirms the test above is a real gate, not one
    ///     that would pass regardless of whether the job runs.
    /// </summary>
    [Fact]
    public async Task StartupPass_WithNoEmbedJobRegistered_LeavesRowsPending()
    {
        await SeedPendingRowsAsync(3);
        await ConfigureProviderAsync();
        var service = ServiceWith([]);

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        (await service.Ticks.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(BankMaintenanceHostedService.OnDemandPollInterval);
            await service.OnDemandPolls.WaitAsync(i + 1, TimeSpan.FromMilliseconds(200),
                TestContext.Current.CancellationToken);
        }

        (await PendingCountAsync()).ShouldBeGreaterThan(0);

        run.IsFaulted.ShouldBeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedPendingRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
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

    private async Task ConfigureProviderAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> PendingCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE embed_state = 'pending'",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private BankMaintenanceHostedService ServiceWith(IReadOnlyList<IMaintenanceJob> jobs) =>
        new(_factory, _time, TestTelemetry.None, new FakeLogger<BankMaintenanceHostedService>(),
            NoOpNoiseEntryStore.Instance, new SqlitePromotionQueueStore(_factory, _time),
            new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance),
            new MaintenanceJobRunner(_time, NullLogger<MaintenanceJobRunner>.Instance), jobs);
}
