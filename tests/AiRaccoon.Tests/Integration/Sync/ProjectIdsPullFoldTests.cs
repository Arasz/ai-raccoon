using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using AiRaccoon.Tests.Unit.Projects;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     Air-merge P2 convergence: the durable P1 alias map is consulted by pull/MergeRemoteAsync, so
///     an offline replica's push after a repair converges instead of resurrecting loser ids.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-tombstone-PK-rewrite :
///         TombstonePkRewritten_NoResurrectionOnRealPull : loser tombstone + live loser row on the
///         unrepaired side; skip-pull-fold : PullFoldsLiveLoserRows_IntoTheWinner (2 live loser rows),
///         PullFoldsGuidLoserRows_IntoTheWinner (guid + name row + guid tombstone),
///         PullDedupContent_SurvivorPersistsUnderWinner (same hash both sides) — a hand-SQL replay of
///         the merge is a fake-store green, so all four drive the real MergeRemoteAsync through
///         MemorySyncAsync. Code never syncs (P1 trace (b)), so the fold covers entries +
///         tombstones only.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectIdsPullFoldTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private static string FixtureMapJson() => FixtureMap().ToJson();

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _localRoot = TestData.CreateTempRoot("project-ids-pull-local");
    private readonly string _remoteRoot = TestData.CreateTempRoot("project-ids-pull-remote");

    public void Dispose()
    {
        // SyncService loads the durable map into process-wide Default during merges (E);
        // reset so later collection members start from the empty steady state.
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_localRoot);
        TestData.DeleteTempRoot(_remoteRoot);
    }

    /// <summary>
    ///     Seed a loser-id delete + tombstone, repair (PK rewrites to the winner), then pull from
    ///     an unrepaired replica still holding the live loser row: the delete stays deleted.
    ///     Scoped to the tombstone leg (review MUST-5): single-surface fixture (1 id × 1 row) —
    ///     multi-surface cover is Repair_FirstRunMovesRows_SecondRunNoOp, cited not duplicated.
    ///     Ledger — skip-tombstone-PK-rewrite : --filter TombstonePkRewritten_NoResurrectionOnRealPull :
    ///     loser tombstone + live loser row on the unrepaired side; skip-pull-fold : same : same fixture.
    /// </summary>
    [RetryFact]
    public async Task TombstonePkRewritten_NoResurrectionOnRealPull()
    {
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-del', 'h-del', 'doomed content', 'seed.md', 'project', @loser, 'ctx-a', 1, 1, 'pending')",
                new { loser = Loser });
            await local.ExecuteAsync("DELETE FROM entries WHERE hash = 'h-del'");
            await local.ExecuteAsync(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@loser, 'h-del', 'project', 2)",
                new { loser = Loser });
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        await using (var check = await localFactory.OpenBankAsync(ct))
        {
            (await check.ExecuteScalarAsync<string>(
                    "SELECT project_id FROM sync_tombstones WHERE hash = 'h-del'"))
                .ShouldBe(Winner, "the repair rewrites the tombstone PK to the winner");
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-del', 'h-del', 'doomed content', 'seed.md', 'project', @loser, 'ctx-a', 1, 1, 'pending')",
                new { loser = Loser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash = 'h-del'"))
            .ShouldBe(0, "the repaired delete stays deleted — the unrepaired replica's loser push stays suppressed");
    }

    /// <summary>
    ///     Live loser rows pulled from an unrepaired replica land folded under the winner —
    ///     the loser id never reappears as canonical. Pull-fold-only (review MUST-6): reddens on
    ///     skip-pull-fold, never on skip-repair — the repair is setup here, not the subject.
    ///     Ledger — skip-pull-fold : --filter PullFoldsLiveLoserRows_IntoTheWinner : 2 live loser rows;
    ///     single-row fixture would pass while dropping a side, hence two.
    /// </summary>
    [RetryFact]
    public async Task PullFoldsLiveLoserRows_IntoTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
                "('h-live-1', 'h-live-1', 'live content one', 'seed.md', 'project', @loser, 'ctx-a', 1, 1, 'pending')," +
                "('h-live-2', 'h-live-2', 'live content two', 'seed.md', 'project', @loser, 'ctx-a', 2, 2, 'pending')",
                new { loser = Loser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash IN ('h-live-1', 'h-live-2')"))
            .ShouldBe(2);
        (await verify.ExecuteScalarAsync<string>("SELECT DISTINCT project_id FROM entries WHERE hash IN ('h-live-1', 'h-live-2')"))
            .ShouldBe(Winner);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @loser", new { loser = Loser }))
            .ShouldBe(0, "the loser id never reappears as canonical after a pull");
    }

    /// <summary>
    ///     Guid loser rows pulled from an unrepaired replica fold through the REMOTE projects-row
    ///     name (review MUST-3): the live 01a062f4 guid is registered under its pre-guid name, which
    ///     the static verbatim CASE alone can never match — without the name lookup the guid leaks
    ///     back as canonical. The guid tombstone leg folds symmetrically at the tombstone sites.
    ///     Ledger — static-verbatim-only-fold : --filter PullFoldsGuidLoserRows_IntoTheWinner : guid
    ///     entries + projects name row + guid tombstone on the unrepaired side (raw guid reappears → red).
    /// </summary>
    [RetryFact]
    public async Task PullFoldsGuidLoserRows_IntoTheWinner()
    {
        const string guidLoser = "01a062f4-0000-7000-8000-000000000001";
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-guid-live', 'h-guid-live', 'guid content', 'seed.md', 'project', @guid, 'ctx-a', 1, 1, 'pending')",
                new { guid = guidLoser });
            await remote.ExecuteAsync(
                "INSERT INTO projects (id, name, created_at) VALUES (@guid, @loser, 1)",
                new { guid = guidLoser, loser = Loser });
            await remote.ExecuteAsync(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@guid, 'h-guid-gone', 'project', 2)",
                new { guid = guidLoser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-guid-live'"))
            .ShouldBe(Winner, "the guid row folds through its projects-row name into the winner");
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @guid", new { guid = guidLoser }))
            .ShouldBe(0, "the guid never reappears as canonical after a pull");
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM sync_tombstones WHERE hash = 'h-guid-gone'"))
            .ShouldBe(Winner, "the guid tombstone folds symmetrically to the winner key");
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM sync_tombstones WHERE project_id = @guid", new { guid = guidLoser }))
            .ShouldBe(0);
    }

    /// <summary>
    ///     Dedup-shaped content converging from an unrepaired replica: the same hash lives under the
    ///     winner locally and under the loser remotely. The pull folds and content-dedups to a single
    ///     survivor — and nothing suppresses it, because the fixed repair creates no tombstone for
    ///     dedup-collapsed duplicates (review MUST-2; the absence itself is pinned by
    ///     Repair_FirstRunMovesRows_SecondRunNoOp's dup-hash-1/qdup-1 asserts).
    ///     Ledger — skip-pull-fold : --filter PullDedupContent_SurvivorPersistsUnderWinner : same hash on
    ///     both sides (raw loser insert → count 2 / loser present → red).
    /// </summary>
    [RetryFact]
    public async Task PullDedupContent_SurvivorPersistsUnderWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-dup', 'h-dup', 'shared content', 'seed.md', 'project', @winner, 'ctx-a', 1, 1, 'pending')",
                new { winner = Winner });
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        await using (var prePull = await localFactory.OpenBankAsync(ct))
        {
            (await prePull.ExecuteScalarAsync<long>("SELECT count(*) FROM sync_tombstones WHERE hash = 'h-dup'"))
                .ShouldBe(0, "the repair suppresses nothing for content that survives");
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-dup', 'h-dup', 'shared content', 'seed.md', 'project', @loser, 'ctx-a', 1, 1, 'pending')",
                new { loser = Loser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash = 'h-dup'"))
            .ShouldBe(1, "the duplicate converges to a single survivor");
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-dup'"))
            .ShouldBe(Winner);
    }

    /// <summary>
    ///     d-426 SHOULD-3 self-arm (the MUST-9 static-alias gap made explicit): a remote guid row
    ///     whose projects-row name resolves to the WINNER — not an alias — lands on the winner.
    ///     Without the self-arm the CASE falls to ELSE and the raw guid leaks back as canonical.
    ///     Ledger — winner-named-guid-leak : --filter PullFoldsWinnerNamedGuidRow_IntoTheWinner :
    ///     guid + winner-name row, no alias hop (raw guid reappears → red).
    /// </summary>
    [RetryFact]
    public async Task PullFoldsWinnerNamedGuidRow_IntoTheWinner()
    {
        const string guidWinnerNamed = "01a062f4-0000-7000-8000-000000000099";
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
                "('h-guid-winner', 'h-guid-winner', 'winner-named guid content', 'seed.md', 'project', @guid, 'ctx-a', 1, 1, 'pending')",
                new { guid = guidWinnerNamed });
            await remote.ExecuteAsync(
                "INSERT INTO projects (id, name, created_at) VALUES (@guid, @winner, 1)",
                new { guid = guidWinnerNamed, winner = Winner });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-guid-winner'"))
            .ShouldBe(Winner, "the winner-named guid row lands on the winner, not the raw guid");
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @guid", new { guid = guidWinnerNamed }))
            .ShouldBe(0, "the guid never reappears as canonical after a pull");
    }

    /// <summary>
    ///     The pull fold matches the repair's executable domain, which D1 broadened past d-426
    ///     SHOULD-4's project-scope-only shape: custom-scope loser rows fold to the winner like
    ///     every other committed row, shared rows stay verbatim (cross-project, never folded).
    ///     A pull that left custom rows under the loser id would re-seed on every sync from an
    ///     unrepaired peer the very rows the repair had just folded — a repaired bank could never
    ///     hold the D6 (iii) stability verdict.
    ///     Ledger — custom-shared-pull-fold : --filter PullFoldsCustomLoserRows_AndLeavesSharedVerbatim :
    ///     narrow the pull fold back to scope = 'project' → the custom assert goes red; fold shared
    ///     too (drop the scope predicate) → the shared assert goes red.
    /// </summary>
    [RetryFact]
    public async Task PullFoldsCustomLoserRows_AndLeavesSharedVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            await RepairJob().RunAsync(local, ct);
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
                "('h-custom', 'h-custom', 'custom loser content', 'seed.md', 'custom', @loser, 'ctx-a', 1, 1, 'pending')," +
                "('h-shared', 'h-shared', 'shared loser content', 'seed.md', 'shared', @loser, 'ctx-a', 2, 2, 'pending')",
                new { loser = Loser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-custom'"))
            .ShouldBe(Winner, "custom-scope rows are committed rows — D1 folds them with the rest");
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-shared'"))
            .ShouldBe(Loser, "shared-scope rows merge verbatim — the repair never folds them");
    }

    private static SqliteConnectionFactory Factory(string dataRoot)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private static string BankPath(string dataRoot) =>
        SqliteConnectionFactory.BankPathFor(TestData.CreateInfrastructureOptions(dataRoot));

    private static ProjectIdsRepairJob RepairJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));

    /// <summary>
    ///     The factory banks run WAL: file bytes alone miss the -wal frames, so checkpoint first
    ///     (the service's own push path VACUUMs for the same reason).
    /// </summary>
    private static async Task<byte[]> SnapshotBytesAsync(string dataRoot, CancellationToken ct)
    {
        await using var checkpoint = new SqliteConnection($"Data Source={BankPath(dataRoot)}");
        await checkpoint.OpenAsync(ct);
        await checkpoint.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        return await File.ReadAllBytesAsync(BankPath(dataRoot), ct);
    }

    private static SyncService NewSyncService(FakeCloudStore cloud, SqliteConnectionFactory localFactory) =>
        new(cloud,
            ct => localFactory.OpenBankAsync(ct),
            OpenSnapshotWithVectorAsync,
            OpenSnapshotWithVectorAsync,
            new FakeTimeProvider(FixedNow), NullLogger<SyncService>.Instance,
            aliasMap: FixtureMap()); // QA F2: SyncService shares the repair FakeTimeProvider — merge watermark and tombstone GC stay deterministic.

    /// <summary>
    ///     Mirrors production snapshot opens (AppRegistrations): the strip DELETEs entries rows,
    ///     which fires the vec triggers — without the vec0 module loaded that DELETE throws.
    /// </summary>
    private static async Task<SqliteConnection> OpenSnapshotWithVectorAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
