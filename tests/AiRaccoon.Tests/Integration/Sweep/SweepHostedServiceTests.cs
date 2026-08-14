using AiRaccoon.Core.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Sweep;

/// <summary>
///     The reaper contract over a real bank: a tick actually deletes the expired row, and the
///     kill switch (sweep.enabled.global=false) leaves it alone. Signal-driven (TickSignal +
///     FakeTimeProvider), no sleeps.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SweepHostedServiceTests : IDisposable
{
    private const string ProjectId = "acme";

    /// <summary>Longer than the entry's 1-day TTL, so one tick both fires and expires the row.</summary>
    private const int IntervalHours = 48;

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot = TestData.CreateTempRoot("sweep-hosted-service");
    private readonly SweepHostedService _service;
    private readonly SqliteMemoryStore _store;
    private readonly FakeTimeProvider _time;

    public SweepHostedServiceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(factory,
            new FakeLogger<SqliteMemoryStore>(), new SqliteMemorySourceStore(factory), new StubChunker(), _time, TestData.CreateEmbeddingService());
        _service = new SweepHostedService(_store, new SweepService(_store, _time), _time,
            TestTelemetry.None, new FakeLogger<SweepHostedService>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    [Fact]
    public async Task ReaperTick_DeletesTheExpiredRow()
    {
        var hash = await SeedExpiringEntryAsync();

        await RunOneTickAsync();

        (await RemainingHashesAsync()).ShouldNotContain(hash,
            "the reaper tick must delete the expired row, not merely report it");
    }

    [Fact]
    public async Task ReaperTick_WhenSweepIsDisabled_LeavesTheRowPresent()
    {
        var hash = await SeedExpiringEntryAsync();
        await _store.SetSettingAsync(SweepConfigKeys.EnabledGlobal, "false",
            TestContext.Current.CancellationToken);

        await RunOneTickAsync();

        (await RemainingHashesAsync()).ShouldContain(hash);
    }

    /// <summary>
    ///     Writes one entry, gives it a 1-day TTL, and raises the threshold above the stored
    ///     rating: ratings only move on a search hit, so an unsearched entry sits at 0.5 forever.
    ///     Also opts the project into full access mode (H6): the reaper now honours the same
    ///     Destructive requirement memory_sweep enforces at the MCP boundary, so a project that
    ///     has not consented no longer gets swept just because the caller is a timer.
    /// </summary>
    private async Task<string> SeedExpiringEntryAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "an expiring note"),
            cancellationToken);
        (await _store.SetEntryTtlAsync(ProjectId, entry.Hash, EntryTtl.MinDays, cancellationToken))
            .ShouldBeTrue();
        await _store.SetSettingAsync(SweepThreshold.SettingKey, "0.9", cancellationToken);
        await _store.SetSettingAsync(SweepConfigKeys.IntervalHoursGlobal,
            IntervalHours.ToString(), cancellationToken);
        await _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(ProjectId), "full", cancellationToken);
        return entry.Hash;
    }

    /// <summary>Start, wait for the timer to arm, advance exactly one interval, wait for the pass.</summary>
    private async Task RunOneTickAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _service.StartAsync(cancellationToken);
        (await _service.TimerArmed.WaitAsync(1, SignalTimeout, cancellationToken)).ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(IntervalHours));

        (await _service.Ticks.WaitAsync(1, SignalTimeout, cancellationToken)).ShouldBeTrue();
        await _service.StopAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> RemainingHashesAsync()
    {
        var entries = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        return [.. entries.Select(e => e.Hash)];
    }

    private sealed class StubChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => [text];
    }
}
