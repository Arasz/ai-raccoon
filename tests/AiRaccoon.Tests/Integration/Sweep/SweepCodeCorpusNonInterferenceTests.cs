using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
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

    /// <summary>
    ///     Integration review small item 3: this test previously asserted only code_entries and
    ///     code_fts survive. Added a vec_code assertion too (the code row is now seeded through
    ///     embed_state='embedded' with a real vector, so vec_code actually holds a row for the
    ///     sweep to threaten — the old pending-state seed left vec_code empty regardless of
    ///     whether the sweep interfered, so an assertion on it there would have proven nothing).
    ///
    ///     On a plausible RED for "sweep touches code rows": SweepService (src/AiRaccoon.Infrastructure/
    ///     Degradation/SweepService.cs) never issues SQL of its own against a table name — it goes
    ///     through IMemoryStore.ListContextAsync/DeleteInScopeAsync, both of which are hard-coded
    ///     to the `entries` table inside SqliteMemoryStore. There is no query string, table-name
    ///     parameter, or shared helper a mis-scoping bug could corrupt into reaching code_entries;
    ///     the abstraction boundary between "sweep" and "code corpus" is a different C# type, not a
    ///     WHERE clause. A synthetic "make the sweep aggressive enough to catch a mis-scoped
    ///     DELETE" scenario therefore has no honest RED to demonstrate — the only way this test
    ///     could ever fail is a change that makes SweepService (or something it calls) start
    ///     touching code_entries directly, which this test already catches via the counts below.
    /// </summary>
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
        (await VecCodeCountAsync(cancellationToken)).ShouldBe(1L,
            "the sweep must never touch vec_code either.");
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

    /// <summary>Drives the row to embed_state='embedded' with a real 768-float vector, so vec_code
    /// actually holds a row (the vec_code_au trigger fires) for the sweep to threaten.</summary>
    private async Task SeedCodeRowAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_bankPath}");
        await conn.OpenAsync(cancellationToken);
        conn.EnableExtensions();
        conn.LoadVector();

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
                             INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                             VALUES (1, 'code-hash-1', 'src/foo.cs', 'public sealed class Foo { }', 'src/foo.cs', 1, 1, @projectId, 1, 1)
                             """;
        insert.Parameters.AddWithValue("@projectId", ProjectId);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1";
        update.Parameters.AddWithValue("@embedding", EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray()));
        await update.ExecuteNonQueryAsync(cancellationToken);
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

    private async Task<long> VecCodeCountAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection($"Data Source={_bankPath}");
        await conn.OpenAsync(cancellationToken);
        conn.EnableExtensions();
        conn.LoadVector();
        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM vec_code WHERE rowid = 1";
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Start, wait for the timer to arm, advance exactly one interval, wait for the pass.</summary>
    private async Task RunOneTickAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _service.StartAsync(cancellationToken);
        await _service.TimerArmed.WaitAsync(1, cancellationToken);

        _time.Advance(TimeSpan.FromHours(IntervalHours));

        await _service.Ticks.WaitAsync(1, cancellationToken);
        await _service.StopAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> RemainingHashesAsync()
    {
        var entries = await _store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        return [.. entries.Select(e => e.Hash)];
    }
}
