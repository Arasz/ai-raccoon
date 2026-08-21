using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Sweep;

/// <summary>
///     WP7 non-interference (docs/work/2026-08-21-code-search-implementation-plan.md §3.8; QA
///     catalog WP7-T03 "Sweep_DoesNotTouchCodeRows" — the code corpus has no TTL/degradation, so
///     the reaper must never see or touch code rows. Mirrors <see cref="SweepHostedServiceTests" />
///     but adds a seeded code row alongside the expiring memory row.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SweepCodeCorpusNonInterferenceTests : IDisposable
{
    private const string ProjectId = "acme";

    /// <summary>Longer than the entry's 1-day TTL, so one tick both fires and expires the row.</summary>
    private const int IntervalHours = 48;

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private readonly string _bankPath;
    private readonly string _dataRoot = TestData.CreateTempRoot("sweep-code-non-interference");
    private readonly SweepHostedService _service;
    private readonly SqliteMemoryStore _store;
    private readonly FakeTimeProvider _time;

    public SweepCodeCorpusNonInterferenceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _bankPath = factory.BankPath;
        _time = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(factory,
            new FakeLogger<SqliteMemoryStore>(), new SqliteMemorySourceStore(factory), new SingleChunkChunker(), _time, TestData.CreateEmbeddingService());
        _service = new SweepHostedService(_store, new SweepService(_store, _time), _time,
            TestTelemetry.None, new FakeLogger<SweepHostedService>());
    }

    public void Dispose()
    {
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public async Task ReaperTick_DeletesTheExpiredMemoryRow_ButLeavesTheCodeRowUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var hash = await SeedExpiringEntryAsync();
        await SeedCodeRowAsync(cancellationToken);

        await RunOneTickAsync();

        (await RemainingHashesAsync()).ShouldNotContain(hash,
            "sanity: the reaper tick must actually delete the expired memory row for this test to mean anything");
        (await CodeEntriesCountAsync(cancellationToken)).ShouldBe(1L,
            "the sweep must never touch code_entries — the code corpus has no TTL/degradation (§3.8).");
        (await CodeFtsCountAsync(cancellationToken)).ShouldBe(1L,
            "the sweep must never touch code_fts either.");
    }

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

    private async Task SeedCodeRowAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_bankPath}");
        await conn.OpenAsync(cancellationToken);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
                             INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                             VALUES (1, 'code-hash-1', 'src/foo.cs', 'public sealed class Foo { }', 'src/foo.cs', 1, 1, @projectId, 1, 1)
                             """;
        insert.Parameters.AddWithValue("@projectId", ProjectId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CodeEntriesCountAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_bankPath}");
        await conn.OpenAsync(cancellationToken);
        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM code_entries";
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<long> CodeFtsCountAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_bankPath}");
        await conn.OpenAsync(cancellationToken);
        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM code_fts WHERE code_fts MATCH 'Foo'";
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
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
}
