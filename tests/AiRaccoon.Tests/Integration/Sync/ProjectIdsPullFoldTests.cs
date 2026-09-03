using AiRaccoon.Core.Ingestion;
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
///         unrepaired side; skip-pull-fold : either test : same fixtures — a hand-SQL replay of the
///         merge is a fake-store green, so both drive the real MergeRemoteAsync through
///         MemorySyncAsync. Code never syncs (P1 trace (b)), so the fold covers entries +
///         tombstones only.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsPullFoldTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _localRoot = TestData.CreateTempRoot("project-ids-pull-local");
    private readonly string _remoteRoot = TestData.CreateTempRoot("project-ids-pull-remote");

    public void Dispose()
    {
        TestData.DeleteTempRoot(_localRoot);
        TestData.DeleteTempRoot(_remoteRoot);
    }

    /// <summary>
    ///     Seed a loser-id delete + tombstone, repair (PK rewrites to the winner), then pull from
    ///     an unrepaired replica still holding the live loser row: the delete stays deleted.
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
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds() });
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
    ///     A live loser row pulled from an unrepaired replica lands folded under the winner —
    ///     the loser id never reappears as canonical.
    /// </summary>
    [RetryFact]
    public async Task PullFoldsLiveLoserRows_IntoTheWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        var localFactory = Factory(_localRoot);
        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await local.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds() });
            await RepairJob().RunAsync(local, ct);
        }

        var remoteFactory = Factory(_remoteRoot);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('h-live', 'h-live', 'live content', 'seed.md', 'project', @loser, 'ctx-a', 1, 1, 'pending')",
                new { loser = Loser });
        }

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await SnapshotBytesAsync(_remoteRoot, ct));
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, "test-object", ct);

        await using var verify = await localFactory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash = 'h-live'"))
            .ShouldBe(1);
        (await verify.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-live'"))
            .ShouldBe(Winner);
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @loser", new { loser = Loser }))
            .ShouldBe(0, "the loser id never reappears as canonical after a pull");
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
            TimeProvider.System, NullLogger<SyncService>.Instance);

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
