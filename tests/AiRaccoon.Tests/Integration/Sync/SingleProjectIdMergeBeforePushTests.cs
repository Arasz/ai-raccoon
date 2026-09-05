using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
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
///     Air-merge P-INT fleet rule: merge-before-push on every replica, with the P1 per-replica
///     local-repair rule (projects rows never sync, so an unmerged replica's pull would re-insert
///     loser-id entries its own never-synced projects table still legitimizes). Two file-backed
///     replicas and one <see cref="FakeCloudStore" /> drive the REAL
///     <see cref="SyncService.MemorySyncAsync" /> in both directions — a hand-SQL replay of the
///     merge is a fake-store green. The guid-tombstone leg is the explicit P-INT row for the
///     tracked static-alias gap (review d-401 MUST-9).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class SingleProjectIdMergeBeforePushTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string GuidLoser = "01a062f4-0000-7000-8000-000000000001";
    private const string ObjectKey = "test-object";

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _localRoot = TestData.CreateTempRoot("single-project-id-push-local");
    private readonly string _remoteRoot = TestData.CreateTempRoot("single-project-id-push-remote");

    public void Dispose()
    {
        // SyncService loads the durable map into process-wide Default during merges (E);
        // reset so later collection members start from the empty steady state.
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_localRoot);
        TestData.DeleteTempRoot(_remoteRoot);
    }

    /// <summary>
    ///     The full fleet cycle: A (winner rows) pushes to the empty cloud; offline B (loser rows +
    ///     a unique winner row) pulls A, repairs LOCALLY per the fleet rule, pushes the converged
    ///     bank; A pulls B's content. Both banks and the cloud snapshot end loser-free with every
    ///     unique row present under the winner.
    ///     Ledger — revert-jsaa-fold : --filter "FullyQualifiedName~SingleProjectIdMergeBeforePushTests.TwoReplicas_OfflinePushAfterRepair_Converges" :
    ///     A 2 winner rows, B 2 loser + 1 unique winner rows, real sync both directions with a local
    ///     repair on B between (unfolded loser rows survive on A and in the cloud → red).
    /// </summary>

    private static string FixtureMapJson() =>
        new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
            ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
            ["qa-noise-project", "manual-sweep"]).ToJson();

    [RetryFact]
    public async Task TwoReplicas_OfflinePushAfterRepair_Converges()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = new FakeCloudStore();
        var localFactory = Factory(_localRoot);
        var remoteFactory = Factory(_remoteRoot);

        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await EntryAsync(local, "a-w1", Winner, "ctx-a", "alpha winner content", ct);
            await EntryAsync(local, "a-w2", Winner, "ctx-a", "beta winner content", ct);
        }

        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, ObjectKey, ct);

        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await EntryAsync(remote, "b-l1", Loser, "ctx-a", "gamma loser content", ct);
            await EntryAsync(remote, "b-l2", Loser, "ctx-a", "delta loser content", ct);
            await EntryAsync(remote, "b-w1", Winner, "ctx-a", "epsilon bravo content", ct);
        }

        // B pulls A's push (merge #1), then repairs locally BEFORE pushing (the fleet rule —
        // B's never-synced projects table would otherwise keep legitimizing the loser id).
        await NewSyncService(cloud, remoteFactory).MemorySyncAsync(Winner, ObjectKey, ct);
        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await remote.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            (await RepairJob().RunAsync(remote, ct)).ShouldBeTrue("B holds labeled loser rows to fold");
        }

        await NewSyncService(cloud, remoteFactory).MemorySyncAsync(Winner, ObjectKey, ct);
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, ObjectKey, ct);

        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            (await LoserCountAsync(local, ct)).ShouldBe(0, "the loser id never reappears as canonical on A");
            (await WinnerHashesAsync(local, ct)).ShouldBe(["a-w1", "a-w2", "b-l1", "b-l2", "b-w1"],
                "A converges to the full content union under the winner");
        }

        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            (await LoserCountAsync(remote, ct)).ShouldBe(0, "B's local repair holds after the push");
            (await WinnerHashesAsync(remote, ct)).ShouldBe(["a-w1", "a-w2", "b-l1", "b-l2", "b-w1"]);
        }

        await using var snapshot = await OpenCloudSnapshotAsync(cloud, ct);
        (await snapshot.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @loser", new { loser = Loser }))
            .ShouldBe(0, "the pushed cloud snapshot itself is loser-free — merge ran before push");
        (await snapshot.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @winner", new { winner = Winner }))
            .ShouldBe(5);
    }

    /// <summary>
    ///     The tracked static-alias gap as an explicit P-INT row: an offline replica holding a guid
    ///     entry (registered under its pre-guid name), a guid tombstone and a live loser entry
    ///     repairs locally and pushes; the guid folds through the projects-row name at every site
    ///     and never reappears as canonical — on B, on A after pulling, or in the cloud snapshot.
    ///     Ledger — revert-jsaa-fold : --filter "FullyQualifiedName~SingleProjectIdMergeBeforePushTests.OfflineReplica_GuidTombstone_FoldsThroughPush" :
    ///     guid entry + projects name row + guid tombstone + live loser entry on the offline side
    ///     (the name→alias second hop breaks → guid-canonical rows survive → red).
    /// </summary>
    [RetryFact]
    public async Task OfflineReplica_GuidTombstone_FoldsThroughPush()
    {
        var ct = TestContext.Current.CancellationToken;
        var cloud = new FakeCloudStore();
        var localFactory = Factory(_localRoot);
        var remoteFactory = Factory(_remoteRoot);

        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            await EntryAsync(local, "a-w1", Winner, "ctx-a", "alpha winner content", ct);
        }

        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, ObjectKey, ct);

        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            await EntryAsync(remote, "h-guid-live", GuidLoser, "ctx-a", "guid content", ct);
            await remote.ExecuteAsync(
                "INSERT INTO projects (id, name, created_at) VALUES (@guid, @loser, 1)",
                new { guid = GuidLoser, loser = Loser });
            await remote.ExecuteAsync(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@guid, 'h-guid-gone', 'project', 2)",
                new { guid = GuidLoser });
            await EntryAsync(remote, "h-live", Loser, "ctx-a", "live loser content", ct);
            await remote.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() });
            (await RepairJob().RunAsync(remote, ct)).ShouldBeTrue("the offline replica folds before pushing");
        }

        await NewSyncService(cloud, remoteFactory).MemorySyncAsync(Winner, ObjectKey, ct);
        await NewSyncService(cloud, localFactory).MemorySyncAsync(Winner, ObjectKey, ct);

        await using (var remote = await remoteFactory.OpenBankAsync(ct))
        {
            (await remote.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @guid", new { guid = GuidLoser }))
                .ShouldBe(0, "the guid never reappears as canonical on the repaired replica");
            (await remote.ExecuteScalarAsync<string>("SELECT project_id FROM sync_tombstones WHERE hash = 'h-guid-gone'"))
                .ShouldBe(Winner, "the guid tombstone folds symmetrically to the winner key");
        }

        await using (var local = await localFactory.OpenBankAsync(ct))
        {
            (await local.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-guid-live'"))
                .ShouldBe(Winner, "the guid row reaches A folded through its projects-row name");
            (await local.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id IN (@guid, @loser)", new { guid = GuidLoser, loser = Loser }))
                .ShouldBe(0);
            (await local.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash = 'h-live'"))
                .ShouldBe(Winner);
        }

        await using var snapshot = await OpenCloudSnapshotAsync(cloud, ct);
        (await snapshot.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE project_id = @guid", new { guid = GuidLoser }))
            .ShouldBe(0, "the cloud snapshot carries no guid-canonical rows");
        (await snapshot.ExecuteScalarAsync<string>("SELECT project_id FROM sync_tombstones WHERE hash = 'h-guid-gone'"))
            .ShouldBe(Winner);
    }

    private static async Task EntryAsync(SqliteConnection connection, string hash, string projectId, string label, string value, CancellationToken ct) =>
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @value, 'seed.md', 's', 'project', @projectId, @label, 1, 1, 'pending')",
                new { hash, projectId, label, value }, cancellationToken: ct));

    private static async Task<long> LoserCountAsync(SqliteConnection connection, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM entries WHERE project_id = @loser", new { loser = Loser }, cancellationToken: ct));

    private static async Task<List<string>> WinnerHashesAsync(SqliteConnection connection, CancellationToken ct) =>
        (await connection.QueryAsync<string>(
            new CommandDefinition("SELECT hash FROM entries WHERE project_id = @winner ORDER BY hash", new { winner = Winner }, cancellationToken: ct)))
        .ToList();

    private static SqliteConnectionFactory Factory(string dataRoot)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private static ProjectIdsRepairJob RepairJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));

    private static SyncService NewSyncService(FakeCloudStore cloud, SqliteConnectionFactory localFactory) =>
        new(cloud,
            ct => localFactory.OpenBankAsync(ct),
            OpenSnapshotWithVectorAsync,
            OpenSnapshotWithVectorAsync,
            new FakeTimeProvider(FixedNow), NullLogger<SyncService>.Instance,
            aliasMap: FixtureMap()); // QA F2: SyncService shares the repair FakeTimeProvider — merge watermark and tombstone GC stay deterministic.

    private static async Task<SqliteConnection> OpenSnapshotWithVectorAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<SqliteConnection> OpenCloudSnapshotAsync(FakeCloudStore cloud, CancellationToken ct)
    {
        var stored = await cloud.PullAsync(ObjectKey, ct);
        stored.ShouldNotBeNull("the cycle pushes a snapshot to the cloud");
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, stored.Data, ct);
        return await OpenSnapshotWithVectorAsync(path, ct);
    }
}
