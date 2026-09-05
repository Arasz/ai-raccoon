using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests;
using AiRaccoon.Tests.Unit.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     Package E2: the pull arm for <c>project_id_aliases</c> in
///     <see cref="SyncService" /> row-merge style (H2 verdict — the table does not ride free).
///     Remote map rows merge insert-only (alias-PK first-writer-wins); a same-alias-different-winner
///     row is a genuine conflict and surfaces for a human before anything else mutates; a remote
///     without the table (a pre-v14 replica) merges exactly as before. Push folds loser-keyed rows
///     to the winner symmetrically.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class SyncServiceAliasMapTests : IDisposable
{
    private const string Loser = "old-slug";
    private const string Winner = "new-slug";
    private const string Dropped = "qa-noise-project";

    private readonly string _dataRoot = TestData.CreateTempRoot("sync-aliasmap-test");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");
    private string RemotePath => Path.Combine(_dataRoot, "remote.db");

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
        Directory.Delete(_dataRoot, true);
    }

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry(Loser, Winner)],
        [Winner],
        [Dropped]);

    private static string BankDdl(bool withAliasTable) => $$"""
        CREATE TABLE IF NOT EXISTS entries (
            id INTEGER PRIMARY KEY,
            hash TEXT,
            path TEXT,
            value TEXT,
            source_file TEXT NULL,
            section TEXT NULL,
            scope TEXT CHECK(scope IN ('shared','project','custom')) NULL,
            project_id TEXT NULL,
            context_label TEXT NULL,
            workspace_id TEXT NULL,
            agent_id TEXT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            access_count INTEGER NOT NULL DEFAULT 0,
            last_accessed_at INTEGER NULL,
            rating REAL NOT NULL DEFAULT 0.5,
            ttl_days INTEGER NULL,
            embed_state TEXT NOT NULL DEFAULT 'pending',
            embedding BLOB NULL,
            heading_path TEXT NULL,
            structure_embedding BLOB NULL,
            chunk_index INTEGER NOT NULL DEFAULT 0,
            total_chunks INTEGER NOT NULL DEFAULT 0,
            source_id INTEGER NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_shared_bucket
            ON entries(path, hash) WHERE scope = 'shared';
        CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_committed_bucket
            ON entries(path, hash, project_id, scope, COALESCE(context_label, '')) WHERE scope IN ('project', 'custom');
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NULL, created_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_tombstones (project_id TEXT NOT NULL, hash TEXT NOT NULL, scope TEXT NOT NULL,
            deleted_at INTEGER NOT NULL, PRIMARY KEY (project_id, hash, scope));
        CREATE TABLE IF NOT EXISTS memory_source (
            id INTEGER PRIMARY KEY,
            source_type TEXT NOT NULL,
            source_locator TEXT NOT NULL,
            section TEXT NULL,
            heading_path TEXT NULL);
        {{(withAliasTable ? ProjectIdAliases.TableDdl + ";" : "")}}
        """;

    private async Task CreateBankAsync(string path, bool withAliasTable, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BankDdl(withAliasTable);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(string path, string sql, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(string path, string sql, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string?> TextAsync(string path, string sql, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private SyncService NewService(FakeCloudStore cloud, ProjectIdAliasMap? map = null) =>
        new(cloud,
            async ct =>
            {
                var c = new SqliteConnection($"Data Source={BankPath}");
                await c.OpenAsync(ct);
                return c;
            },
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            },
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            },
            TimeProvider.System, NullLogger<SyncService>.Instance, map);

    [RetryFact]
    public async Task MemorySync_PullMergesRemoteAliasRows_AndRefreshesTheCachedDefault()
    {
        // E2 pull arm. Ledger — alias-pull-merge.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: true, ct);
        await ExecAsync(RemotePath,
            "INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES " +
            $"('{Loser}', '{Winner}', 'alias', 1), ('{Dropped}', NULL, 'drop', 1)", ct);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));

        await NewService(cloud).MemorySyncAsync("acme", "test-object", ct);

        (await ScalarAsync(BankPath, "SELECT COUNT(*) FROM project_id_aliases", ct)).ShouldBe(2);
        (await TextAsync(BankPath, $"SELECT winner FROM project_id_aliases WHERE alias = '{Loser}'", ct))
            .ShouldBe(Winner);
        ProjectIdAliasMap.Default.Fold(Loser).ShouldBe(Winner,
            "the pull reloaded the cached choke map off the merged table");
        ProjectIdAliasMap.Default.IsDropped(Dropped).ShouldBeTrue();
    }

    [RetryFact]
    public async Task MemorySync_PullKeepsTheFirstWriter_WhenBothSidesMappedTheAlias()
    {
        // E2 first-writer-wins: the local winner survives the remote's rival row for the same
        // alias — except that shape is the conflict case below, so here the rivals AGREE.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            $"INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES ('{Loser}', '{Winner}', 'alias', 1)",
            ct);
        await ExecAsync(RemotePath,
            "INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES " +
            $"('{Loser}', '{Winner}', 'alias', 2), ('remote-only', 'remote-winner', 'alias', 2)", ct);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));

        await NewService(cloud).MemorySyncAsync("acme", "test-object", ct);

        (await TextAsync(BankPath, $"SELECT winner FROM project_id_aliases WHERE alias = '{Loser}'", ct))
            .ShouldBe(Winner);
        (await TextAsync(BankPath, "SELECT winner FROM project_id_aliases WHERE alias = 'remote-only'", ct))
            .ShouldBe("remote-winner");
    }

    [RetryFact]
    public async Task MemorySync_PullWithSameAliasDifferentWinner_SurfacesConflictBeforeAnythingMutates()
    {
        // E2 conflict surfacing: the exception names the alias and both winners for a human, and
        // fires before the entries merge so the local bank is untouched.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            $"INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES ('{Loser}', '{Winner}', 'alias', 1)",
            ct);
        await ExecAsync(RemotePath,
            $"INSERT INTO project_id_aliases (alias, winner, kind, applied_at) VALUES ('{Loser}', 'rival-winner', 'alias', 2)",
            ct);
        await ExecAsync(RemotePath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));

        var ex = await Should.ThrowAsync<SyncAliasConflictException>(() =>
            NewService(cloud).MemorySyncAsync("acme", "test-object", ct));

        ex.Message.ShouldContain(Loser);
        ex.Message.ShouldContain(Winner);
        ex.Message.ShouldContain("rival-winner");
        (await ScalarAsync(BankPath, "SELECT COUNT(*) FROM entries WHERE hash = 'remote-hash'", ct))
            .ShouldBe(0, "the conflict aborts the merge before entries move");
        (await TextAsync(BankPath, $"SELECT winner FROM project_id_aliases WHERE alias = '{Loser}'", ct))
            .ShouldBe(Winner, "the local first writer still stands");
    }

    [RetryFact]
    public async Task MemorySync_PullFromRemoteWithoutTheTable_MergesExactlyAsBefore()
    {
        // A pre-v14 replica's snapshot has no project_id_aliases: the arm skips, the merge holds.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: false, ct);
        await ExecAsync(RemotePath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));

        await NewService(cloud).MemorySyncAsync("acme", "test-object", ct);

        (await ScalarAsync(BankPath, "SELECT COUNT(*) FROM entries WHERE hash = 'remote-hash'", ct))
            .ShouldBe(1);
        (await ScalarAsync(BankPath, "SELECT COUNT(*) FROM project_id_aliases", ct)).ShouldBe(0);
    }

    [RetryFact]
    public async Task MemorySync_PullThatMergesNoAliasRows_LeavesTheCachedDefaultAlone()
    {
        // E1 says "reload on map change". Default is process-wide, so reloading on every pull
        // instead lets an ordinary sync — one that merged nothing — wipe a map another caller
        // installed, and turns every SyncService test into a silent mutator of shared state.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: true, ct);
        await ExecAsync(RemotePath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'someone-else', 1, 1)", ct);
        var installed = FixtureMap();
        ProjectIdAliasMap.ReplaceDefault(installed);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));
        await NewService(cloud, ProjectIdAliasMap.Empty).MemorySyncAsync("acme", "test-object", ct);

        ProjectIdAliasMap.Default.ShouldBeSameAs(installed,
            "a pull that merged no map rows changed no map — the cache must be left as it was");
    }

    [RetryFact]
    public async Task MemorySync_PushFoldsLoserKeyedRows_ToTheWinnerSymmetrically()
    {
        // E2 push symmetry: an unrepaired replica's loser rows land canonical in the snapshot it
        // uploads, matching what the pull fold would do on the receiving side.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            $"VALUES ('loser-hash', 'loser.md', 'loser content', 'project', '{Loser}', 1, 1)", ct);
        await ExecAsync(BankPath,
            $"INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('{Loser}', 'gone-hash', 'project', 1)",
            ct);

        var cloud = new FakeCloudStore();
        await NewService(cloud, FixtureMap()).MemorySyncAsync("acme", "test-object", ct);

        var pushed = await cloud.PullAsync("test-object", ct);
        pushed.ShouldNotBeNull();
        var snapshotPath = Path.Combine(_dataRoot, "pushed.db");
        await File.WriteAllBytesAsync(snapshotPath, pushed.Data, ct);

        (await TextAsync(snapshotPath, "SELECT project_id FROM entries WHERE hash = 'loser-hash'", ct))
            .ShouldBe(Winner);
        (await TextAsync(snapshotPath,
            "SELECT project_id FROM sync_tombstones WHERE hash = 'gone-hash'", ct)).ShouldBe(Winner);
    }

    [RetryFact]
    public async Task MemorySync_PushFoldsLoserRows_WhenTheWinnerAlreadyHoldsTheSameBucket()
    {
        // Integration joint: the push fold rewrites project_id in place with no dedup guard, while
        // uq_entries_committed_bucket is UNIQUE over (path, hash, project_id, scope, label). A bank
        // holding both spellings of one row — the ordinary shape after pulling a repaired replica —
        // must still push: the repair's own applier dedups (fold-where-no-twin, then delete).
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            $"VALUES ('twin-hash', 'twin.md', 'twin content', 'project', '{Loser}', 1, 1)", ct);
        await ExecAsync(BankPath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            $"VALUES ('twin-hash', 'twin.md', 'twin content', 'project', '{Winner}', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        await NewService(cloud, FixtureMap()).MemorySyncAsync("acme", "test-object", ct);

        var pushed = await cloud.PullAsync("test-object", ct);
        pushed.ShouldNotBeNull();
        var snapshotPath = Path.Combine(_dataRoot, "pushed-twin.db");
        await File.WriteAllBytesAsync(snapshotPath, pushed.Data, ct);

        (await ScalarAsync(snapshotPath, "SELECT COUNT(*) FROM entries WHERE hash = 'twin-hash'", ct))
            .ShouldBe(1);
        (await TextAsync(snapshotPath, "SELECT project_id FROM entries WHERE hash = 'twin-hash'", ct))
            .ShouldBe(Winner);
    }

    [RetryFact]
    public async Task MemorySync_PushFoldsLoserTombstones_WhenTheWinnerAlreadyHoldsTheSameKey()
    {
        // Same joint on sync_tombstones, whose PRIMARY KEY (project_id, hash, scope) collides the
        // same way. The repair's FoldTombstonesAsync merges deleted_at then deletes the loser row.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            $"INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('{Loser}', 'gone-hash', 'project', 5)",
            ct);
        await ExecAsync(BankPath,
            $"INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('{Winner}', 'gone-hash', 'project', 1)",
            ct);

        var cloud = new FakeCloudStore();
        await NewService(cloud, FixtureMap()).MemorySyncAsync("acme", "test-object", ct);

        var pushed = await cloud.PullAsync("test-object", ct);
        pushed.ShouldNotBeNull();
        var snapshotPath = Path.Combine(_dataRoot, "pushed-tombstone-twin.db");
        await File.WriteAllBytesAsync(snapshotPath, pushed.Data, ct);

        (await ScalarAsync(snapshotPath, "SELECT COUNT(*) FROM sync_tombstones WHERE hash = 'gone-hash'", ct))
            .ShouldBe(1);
        (await ScalarAsync(snapshotPath,
            $"SELECT deleted_at FROM sync_tombstones WHERE hash = 'gone-hash' AND project_id = '{Winner}'", ct))
            .ShouldBe(5);
    }

    [RetryFact]
    public async Task MemorySync_PushLeavesSharedScopeRows_UnderTheLoserSpelling()
    {
        // D1: shared rows are cross-project by design (uq_entries_shared_bucket is (path, hash)
        // global) and the repair never folds them — the planner pins shared-keyed-only ids instead.
        // The push fold must honour the same domain or it steals cross-project content off-machine.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            $"VALUES ('shared-hash', 'shared.md', 'shared content', 'shared', '{Loser}', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        await NewService(cloud, FixtureMap()).MemorySyncAsync("acme", "test-object", ct);

        var pushed = await cloud.PullAsync("test-object", ct);
        pushed.ShouldNotBeNull();
        var snapshotPath = Path.Combine(_dataRoot, "pushed-shared.db");
        await File.WriteAllBytesAsync(snapshotPath, pushed.Data, ct);

        (await TextAsync(snapshotPath, "SELECT project_id FROM entries WHERE hash = 'shared-hash'", ct))
            .ShouldBe(Loser);
    }

    [RetryFact]
    public async Task MemorySync_PullFoldsCustomScopeLoserRows_ToTheWinner()
    {
        // D1 made the repair's fold domain project+custom. The pull fold still folds project scope
        // only, so an unrepaired replica's custom-scope loser rows land back under the retired id
        // on every pull — a repaired bank can never stay converged (D6 iii) while a peer pushes.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await CreateBankAsync(RemotePath, withAliasTable: true, ct);
        await ExecAsync(RemotePath,
            "INSERT INTO entries (hash, path, value, scope, project_id, context_label, created_at, updated_at) " +
            $"VALUES ('custom-hash', 'custom.md', 'custom content', 'custom', '{Loser}', 'notes', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        cloud.Set("test-object", await File.ReadAllBytesAsync(RemotePath, ct));
        await NewService(cloud, FixtureMap()).MemorySyncAsync("acme", "test-object", ct);

        (await TextAsync(BankPath, "SELECT project_id FROM entries WHERE hash = 'custom-hash'", ct))
            .ShouldBe(Winner);
    }

    [RetryFact]
    public async Task MemorySync_PushWithEmptyMap_LeavesLoserSpellingsVerbatim()
    {
        // E-AC3 on the push path: no map, no rewrite — the snapshot carries exactly what the bank holds.
        var ct = TestContext.Current.CancellationToken;
        await CreateBankAsync(BankPath, withAliasTable: true, ct);
        await ExecAsync(BankPath,
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            $"VALUES ('loser-hash', 'loser.md', 'loser content', 'project', '{Loser}', 1, 1)", ct);

        var cloud = new FakeCloudStore();
        await NewService(cloud, ProjectIdAliasMap.Empty).MemorySyncAsync("acme", "test-object", ct);

        var pushed = await cloud.PullAsync("test-object", ct);
        pushed.ShouldNotBeNull();
        var snapshotPath = Path.Combine(_dataRoot, "pushed-empty.db");
        await File.WriteAllBytesAsync(snapshotPath, pushed.Data, ct);

        (await TextAsync(snapshotPath, "SELECT project_id FROM entries WHERE hash = 'loser-hash'", ct))
            .ShouldBe(Loser);
    }
}
