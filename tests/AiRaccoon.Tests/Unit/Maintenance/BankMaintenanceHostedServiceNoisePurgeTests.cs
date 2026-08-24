using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Criterion 2 (this task): the maintenance pass purges noise_entries past their retention
///     setting and leaves newer rows — the ADR-0029 TTL that was promised and never built.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankMaintenanceHostedServiceNoisePurgeTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("bank-maintenance-noise-purge");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time;
    private readonly FakeLogger<BankMaintenanceHostedService> _logger;
    private readonly BackgroundTelemetryProbe _probe = new(BankMaintenanceHostedService.OperationName);
    private readonly INoiseEntryStore _noiseEntryStore;
    private readonly BankMaintenanceHostedService _service;

    public BankMaintenanceHostedServiceNoisePurgeTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _logger = new FakeLogger<BankMaintenanceHostedService>();
        _noiseEntryStore = new SqliteNoiseEntryStore(_factory);
        _service = new BankMaintenanceHostedService(_factory, _time, _probe.Telemetry, _logger, _noiseEntryStore,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSearchQualityService>.Instance));
    }

    public void Dispose()
    {
        _probe.Dispose();
        Directory.Delete(_dataRoot, true);
    }

    [Fact]
    public async Task RunOnce_PurgesExpiredNoiseEntries_LeavesNewerRowsUntouched()
    {
        var now = FixedNow.ToUnixTimeSeconds();
        await _noiseEntryStore.RecordAsync(new MemoryWriteRequest("proj-1", "expired noise"), "policy-a",
            expiresAtUnixSeconds: now - 1, nowUnixSeconds: now - 100, TestContext.Current.CancellationToken);
        await _noiseEntryStore.RecordAsync(new MemoryWriteRequest("proj-1", "fresh noise"), "policy-a",
            expiresAtUnixSeconds: now + 100_000, nowUnixSeconds: now, TestContext.Current.CancellationToken);

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        var remaining = await _noiseEntryStore.ListRecentAsync(10, TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(1);
        remaining[0].RequestContent.ShouldBe("fresh noise");
    }

    [Fact]
    public async Task RunOnce_NothingExpired_LeavesEverythingUntouched()
    {
        var now = FixedNow.ToUnixTimeSeconds();
        await _noiseEntryStore.RecordAsync(new MemoryWriteRequest("proj-1", "fresh noise"), "policy-a",
            expiresAtUnixSeconds: now + 100_000, nowUnixSeconds: now, TestContext.Current.CancellationToken);

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        var remaining = await _noiseEntryStore.ListRecentAsync(10, TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(1);
    }

    /// <summary>A purge failure must not abort the checkpoint/vacuum pass — same silent-failure discipline as the pending-embed retry sweep.</summary>
    [Fact]
    public async Task RunOnce_PurgeThrows_PassStillSucceeds()
    {
        var brokenRoot = TestData.CreateTempRoot("bank-maintenance-noise-purge-broken");
        Directory.CreateDirectory(brokenRoot);
        var brokenOptions = TestData.CreateInfrastructureOptions(brokenRoot);
        var brokenFactory = new SqliteConnectionFactory(brokenOptions, NullKeyProvider.Resolver(brokenOptions));
        var throwingStore = new ThrowingNoiseEntryStore();
        var service = new BankMaintenanceHostedService(_factory, _time, _probe.Telemetry, _logger,
            throwingStore,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSearchQualityService>.Instance));

        await Should.NotThrowAsync(() => service.RunOnceAsync(TestContext.Current.CancellationToken));

        Directory.Delete(brokenRoot, true);
    }

    private sealed class ThrowingNoiseEntryStore : INoiseEntryStore
    {
        public Task RecordAsync(MemoryWriteRequest request, string policyName, long expiresAtUnixSeconds,
            long nowUnixSeconds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NoiseEntrySummary(0, new Dictionary<string, int>()));

        public Task<IReadOnlyList<NoiseEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoiseEntry>>([]);

        public Task<int> PurgeExpiredAsync(long nowUnixSeconds, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("purge boom");
    }
}
