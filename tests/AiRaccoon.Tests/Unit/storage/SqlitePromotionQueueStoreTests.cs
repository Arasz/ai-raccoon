using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Persistence contract for promotion_queue: upsert-by-(project, hash), review ordering,
///     discard, stats, and per-project eviction victim query. Rows are separate from entries —
///     never searchable, never counted by memory_stats.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqlitePromotionQueueStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _clock;
    private readonly SqlitePromotionQueueStore _store;

    public SqlitePromotionQueueStoreTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = new SqlitePromotionQueueStore(_factory, _clock);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static QueueCandidate Candidate(string hash, string value, double score,
        string? sourceFile = null, params string[] reasons) =>
        new(hash, $"{hash}.md", value, sourceFile, score, reasons);

    // ------------------------------------------------------------------ schema
    [Fact]
    public async Task FreshBank_CreatesQueueTable_WithUniqueProjectHash()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var table = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'promotion_queue'");
        table.ShouldBe(1);

        var columns = (await connection.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('promotion_queue')")).ToList();
        columns.ShouldContain("project_id");
        columns.ShouldContain("hash");
        columns.ShouldContain("value");
        columns.ShouldContain("score");
        columns.ShouldContain("reasons");
        columns.ShouldContain("created_at");
        columns.ShouldContain("updated_at");

        var indexes = (await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'promotion_queue'")).ToList();
        indexes.ShouldContain("idx_promotion_queue_project");
        indexes.ShouldContain("idx_promotion_queue_score");

        await connection.ExecuteAsync(
            """
            INSERT INTO promotion_queue (project_id, hash, path, value, score, created_at, updated_at)
            VALUES ('p1', 'h1', 'h1.md', 'v', 1.0, 1, 1)
            """);
        var ex = await Should.ThrowAsync<SqliteException>(() => connection.ExecuteAsync(
            """
            INSERT INTO promotion_queue (project_id, hash, path, value, score, created_at, updated_at)
            VALUES ('p1', 'h1', 'h1.md', 'v2', 1.0, 1, 1)
            """));
        ex.Message.ShouldContain("UNIQUE");
    }

    [Fact]
    public async Task QueueRows_NeverLandInEntries()
    {
        await _store.UpsertAsync("acme", [Candidate("h1", "waiting fact", 2.0)], TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var entries = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries");
        entries.ShouldBe(0, "queue rows must live only in promotion_queue");
    }

    // ------------------------------------------------------------------ upsert
    [Fact]
    public async Task Upsert_InsertsNewRows_AndRefreshesExistingWithoutDuplicating()
    {
        var first = await _store.UpsertAsync("acme",
            [Candidate("h1", "fact one", 1.5)], TestContext.Current.CancellationToken);
        first.ShouldBe(1);

        _clock.Advance(TimeSpan.FromDays(1));
        var second = await _store.UpsertAsync("acme",
            [Candidate("h1", "fact one refreshed", 3.0), Candidate("h2", "fact two", 2.0)],
            TestContext.Current.CancellationToken);
        second.ShouldBe(1, "h1 already existed (a refresh, not a new row); only h2 is genuinely new");

        var rows = await _store.ListAsync("acme", TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        var h1 = rows.Single(r => r.Hash == "h1");
        h1.Score.ShouldBe(3.0);
        h1.Value.ShouldBe("fact one refreshed");
        h1.CreatedAt.ShouldBe(FixedNow.ToUnixTimeSeconds(), "the first proposed_at must survive re-propose");
        h1.UpdatedAt.ShouldBe(FixedNow.AddDays(1).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Upsert_ReProposingTheSameHash_ReturnsZero_NotTheConflictUpdateCount()
    {
        var first = await _store.UpsertAsync("acme",
            [Candidate("h1", "fact one", 1.0)], TestContext.Current.CancellationToken);
        first.ShouldBe(1);

        var second = await _store.UpsertAsync("acme",
            [Candidate("h1", "fact one refreshed", 2.0)], TestContext.Current.CancellationToken);

        second.ShouldBe(0, "h1 already occupied a queue slot; re-proposing it must not report queue growth");
    }

    [Fact]
    public async Task Upsert_MidBatchFailure_RollsBackTheWholeBatch()
    {
        var candidates = new List<QueueCandidate>
        {
            Candidate("h1", "would-be-inserted-first", 1.0),
            new("h2", "h2.md", null!, null, 2.0, []), // value NOT NULL violation
            Candidate("h3", "never-reached", 3.0)
        };

        await Should.ThrowAsync<SqliteException>(() =>
            _store.UpsertAsync("acme", candidates, TestContext.Current.CancellationToken));

        (await _store.ListAsync("acme", TestContext.Current.CancellationToken))
            .ShouldBeEmpty("a mid-batch failure must roll back h1 too, not leave a partial propose queued");
    }

    // ------------------------------------------------------------------ list
    [Fact]
    public async Task List_OrdersByScoreDescThenCreatedAtAsc_AndFiltersByProject()
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _store.UpsertAsync("acme",
            [Candidate("h1", "low", 1.0), Candidate("h2", "high", 5.0), Candidate("h3", "mid", 3.0)],
            TestContext.Current.CancellationToken);
        await _store.UpsertAsync("other",
            [Candidate("h4", "other project", 9.0)], TestContext.Current.CancellationToken);

        var acme = await _store.ListAsync("acme", TestContext.Current.CancellationToken);
        acme.Select(r => r.Hash).ShouldBe(["h2", "h3", "h1"]);

        var all = await _store.ListAsync(null, TestContext.Current.CancellationToken);
        all.Select(r => r.Hash).ShouldBe(["h4", "h2", "h3", "h1"]);
    }

    /// <summary>228 of 288 live rows share both score and created_at (a propose pass inserts ~20 rows
    /// within the same second) — SQL specifies no order for those without a further tiebreak.</summary>
    [Fact]
    public async Task List_TiedScoreAndCreatedAt_BreaksTieByInsertionOrder()
    {
        await _store.UpsertAsync("acme",
            [Candidate("z1", "z one", 2.0), Candidate("a1", "a one", 2.0), Candidate("m1", "m one", 2.0)],
            TestContext.Current.CancellationToken);

        var rows = await _store.ListAsync("acme", TestContext.Current.CancellationToken);

        rows.Select(r => r.Hash).ShouldBe(["z1", "a1", "m1"],
            "a tie on score and created_at must still break deterministically, by insertion order");
    }

    [Fact]
    public async Task List_RoundTripsReasonsAndSourceFile()
    {
        await _store.UpsertAsync("acme",
            [Candidate("h1", "fact", 2.0, "docs/plan.md", "cross-project", "recent")],
            TestContext.Current.CancellationToken);

        var row = (await _store.ListAsync("acme", TestContext.Current.CancellationToken)).Single();
        row.SourceFile.ShouldBe("docs/plan.md");
        row.Reasons.ShouldBe(["cross-project", "recent"]);
    }

    // ------------------------------------------------------------------ discard
    [Fact]
    public async Task Discard_RemovesOneRow_OrTheWholeProject()
    {
        await _store.UpsertAsync("acme",
            [Candidate("h1", "a", 1.0), Candidate("h2", "b", 2.0)], TestContext.Current.CancellationToken);

        var one = await _store.DiscardAsync("acme", "h1", TestContext.Current.CancellationToken);
        one.ShouldHaveSingleItem().Hash.ShouldBe("h1", "DELETE...RETURNING hands back the claimed row");
        (await _store.ListAsync("acme", TestContext.Current.CancellationToken)).Select(r => r.Hash)
            .ShouldBe(["h2"]);

        var rest = await _store.DiscardAsync("acme", null, TestContext.Current.CancellationToken);
        rest.ShouldHaveSingleItem().Hash.ShouldBe("h2");
        (await _store.ListAsync("acme", TestContext.Current.CancellationToken)).ShouldBeEmpty();

        var absent = await _store.DiscardAsync("acme", "h1", TestContext.Current.CancellationToken);
        absent.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ stats
    [Fact]
    public async Task GetStats_CountsPerProject_AndAveragesWait()
    {
        await _store.UpsertAsync("acme",
            [Candidate("h1", "a", 1.0), Candidate("h2", "b", 2.0)], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await _store.UpsertAsync("other", [Candidate("h3", "c", 3.0)], TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync(TestContext.Current.CancellationToken);

        stats.TotalCount.ShouldBe(3);
        stats.PerProject.ShouldBe(new Dictionary<string, int> { ["acme"] = 2, ["other"] = 1 });
        // acme's two rows waited 10s, other's row 0s → (10 + 10 + 0) / 3 ≈ 6.67
        stats.AvgWaitSeconds.ShouldNotBeNull();
        stats.AvgWaitSeconds!.Value.ShouldBeInRange(6.6, 6.7);
    }

    [Fact]
    public async Task GetStats_EmptyQueue_HasZeroAndNullWait()
    {
        var stats = await _store.GetStatsAsync(TestContext.Current.CancellationToken);
        stats.TotalCount.ShouldBe(0);
        stats.PerProject.ShouldBeEmpty();
        stats.AvgWaitSeconds.ShouldBeNull();
    }

    // ------------------------------------------------------------------ eviction victim
    [Fact]
    public async Task EvictVictim_RemovesLowestScoreOldestOfTheProject()
    {
        await _store.UpsertAsync("acme",
            [
                Candidate("h1", "low-old", 1.0),
                Candidate("h2", "low-new", 1.0),
                Candidate("h3", "high", 5.0)
            ], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _store.UpsertAsync("acme",
            [Candidate("h2", "low-new refreshed", 1.0)], TestContext.Current.CancellationToken);

        var victim = await _store.EvictVictimAsync("acme", TestContext.Current.CancellationToken);

        victim.ShouldNotBeNull();
        victim!.Hash.ShouldBe("h1", "lowest score first; the older of the two 1.0 rows");
        (await _store.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldNotContain("h1");
    }

    [Fact]
    public async Task EvictVictim_TiedScoreAndCreatedAt_BreaksTieByInsertionOrder()
    {
        await _store.UpsertAsync("acme",
            Enumerable.Range(0, 20).Select(i => Candidate($"h{i:00}", $"fact {i}", 2.0)).ToList(),
            TestContext.Current.CancellationToken);

        var victim = await _store.EvictVictimAsync("acme", TestContext.Current.CancellationToken);

        victim.ShouldNotBeNull();
        victim!.Hash.ShouldBe("h00", "a tie on score and created_at must still break deterministically, by insertion order");
    }

    [Fact]
    public async Task EvictVictim_EmptyProject_ReturnsNull()
    {
        (await _store.EvictVictimAsync("acme", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }
}
