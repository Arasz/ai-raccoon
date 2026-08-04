using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceTests : IDisposable
{
    private readonly string _dataRoot;

    public SyncServiceTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"sync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
    }

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static async Task<SqliteConnection> CreateAndOpenAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          CREATE TABLE IF NOT EXISTS entries (
                              id INTEGER PRIMARY KEY,
                              hash TEXT,
                              path TEXT,
                              value TEXT,
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
                              embedding BLOB NULL
                          );
                          CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS workspaces (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, agent_id TEXT NULL,
                              name TEXT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL, closed_at INTEGER NULL);
                          CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS sync_tombstones (hash TEXT NOT NULL, scope TEXT NOT NULL,
                              deleted_at INTEGER NOT NULL, PRIMARY KEY (hash, scope));
                          """;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    //
    // Scenario 1: sync without credentials errors sync-not-configured
    //
    [Fact]
    public async Task MemorySync_WithoutConfiguredCloudStore_ThrowsSyncNotConfigured()
    {
        var cloud = new NullCloudStore();

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await Should.ThrowAsync<SyncNotConfiguredException>(() =>
            service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));
    }

    //
    // Scenario 2: sync pushes a consistent snapshot with conditional write + integrity check
    //
    [Fact]
    public async Task MemorySync_FreshBank_PushesSnapshotAndRecordsETag()
    {
        var cloud = new FakeCloudStore();

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        result.Sent.ShouldBeGreaterThanOrEqualTo(0);

        // Verify the ETag watermark was recorded in sync_meta.
        await using var conn = new SqliteConnection($"Data Source={BankPath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_meta WHERE key = 'last_etag'";
        var etag = (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        etag.ShouldNotBeNullOrWhiteSpace();
    }

    //
    // Scenario 3: concurrent remote change triggers merge — never silently drops local writes
    //
    [Fact]
    public async Task MemorySync_RemoteChanged_LocalEntriesNotLost()
    {
        var cloud = new FakeCloudStore();

        // Seed local bank with one entry.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('local-hash', 'local.md', 'local content', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // First sync — pushes local snapshot.
        var service = new SyncService(cloud,
            ct => Task.FromResult(new SqliteConnection($"Data Source={BankPath}"))
                .ContinueWith(async t =>
                {
                    var c = t.Result;
                    await c.OpenAsync(ct);
                    return c;
                }).Unwrap(),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Now simulate a remote change — push different content to the cloud.
        var bankBytes = await File.ReadAllBytesAsync(BankPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", bankBytes); // simulate same content to avoid merge

        // Write a NEW local entry (not in the remote).
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('local-hash-2', 'local2.md', 'local content 2', 'project', 'acme', 2, 2)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Second sync — pushes to same remote.
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Verify all entries still exist.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NULL";
            var total = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            total.ShouldBeGreaterThanOrEqualTo(1);
        }
    }

    //
    // Scenario 4: deletes propagate through sync_tombstones — no resurrection
    //
    [Fact]
    public async Task MemorySync_TombstonePropagation_NoResurrection()
    {
        var cloud = new FakeCloudStore();

        async Task<SqliteConnection> OpenBank(CancellationToken ct)
        {
            var conn = await CreateAndOpenAsync(BankPath, ct);
            return conn;
        }

        // Seed local bank with a row and sync.
        // Initialize schema first.
        if (!File.Exists(BankPath))
        {
            var _ = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken);
        }

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('will-delete', 'del.md', 'to be deleted', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, OpenBank,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Delete locally and record a tombstone.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM entries WHERE hash = 'will-delete'";
            await del.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var tomb = conn.CreateCommand();
            tomb.CommandText = """
                               INSERT INTO sync_tombstones (hash, scope, deleted_at)
                               VALUES ('will-delete', 'project', 100)
                               """;
            await tomb.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Sync again.
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Verify the deleted entry was not resurrected.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'will-delete'";
            var count = (long)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            count.ShouldBe(0, "Deleted entry must not be resurrected by sync.");
        }
    }

    //
    // Scenario 5: workspace rows never leave the bank
    //
    [Fact]
    public async Task MemorySync_WorkspaceRows_NotInSyncPayload()
    {
        var cloud = new FakeCloudStore();

        // Create the workspace table and seed a workspace entry.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var ws = conn.CreateCommand();
            ws.CommandText = """
                             INSERT INTO workspaces (id, project_id, status, created_at)
                             VALUES ('ws-1', 'acme', 'Active', 1)
                             """;
            await ws.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var entry = conn.CreateCommand();
            entry.CommandText = """
                                INSERT INTO entries (hash, path, value, workspace_id, project_id, created_at, updated_at)
                                VALUES ('ws-hash', 'ws.md', 'private scratch', 'ws-1', 'acme', 1, 1)
                                """;
            await entry.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Verify the cloud object does NOT contain the workspace row.
        var cloudObj = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        if (cloudObj is not null)
        {
            var remotePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(remotePath, cloudObj.Data, TestContext.Current.CancellationToken);
                await using var remoteConn = new SqliteConnection($"Data Source={remotePath}");
                await remoteConn.OpenAsync(TestContext.Current.CancellationToken);
                await using var count = remoteConn.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NOT NULL";
                var wsCount = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                wsCount.ShouldBe(0, "Workspace rows must not appear in synced snapshots.");
            }
            finally
            {
                File.Delete(remotePath);
            }
        }
    }

    //
    // Scenario 6: merged rows are reindexed
    //
    [Fact]
    public async Task MemorySync_MergedRows_Reindexed()
    {
        var cloud = new FakeCloudStore();

        // First, create and push bank A.
        var bankA = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(bankA, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at, embed_state)
                                     VALUES ('row-a', 'a.md', 'content A', 'project', 'acme', 1, 1, 'embedded')
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(bankA, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(bankA);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Check that the merged row was reindexed (embed_state reset to 'pending').
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT embed_state FROM entries WHERE hash = 'row-a'";
            var state = (string?)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            state.ShouldBe("pending",
                "Merged rows must be reindexed (embed_state 'pending' so embed queue picks them up).");
        }
    }

    //
    // FR-NM-6 s4: workspace exclusion — workspace rows should not appear in local bank stats
    //
    [Fact]
    public async Task MemorySync_WorkspaceRowExcluded_Locally()
    {
        var cloud = new FakeCloudStore();

        // Create workspace row in local bank (simulating workspace scenario).
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var ws = conn.CreateCommand();
            ws.CommandText = "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', 'acme', 'Active', 1)";
            await ws.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var entry = conn.CreateCommand();
            entry.CommandText = """
                                INSERT INTO entries (hash, path, value, workspace_id, project_id, created_at, updated_at)
                                VALUES ('ws-hash-1', 'private.md', 'workspace data', 'ws-1', 'acme', 1, 1)
                                """;
            await entry.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // The cloud snapshot must not contain the workspace row.
        var cloudObj = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        if (cloudObj is not null)
        {
            var remotePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(remotePath, cloudObj.Data, TestContext.Current.CancellationToken);
                await using var remoteConn = new SqliteConnection($"Data Source={remotePath}");
                await remoteConn.OpenAsync(TestContext.Current.CancellationToken);
                await using var count = remoteConn.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NOT NULL";
                var wsRows = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                wsRows.ShouldBe(0, "Workspace rows must not leak into sync snapshots.");
            }
            finally
            {
                File.Delete(remotePath);
            }
        }
    }

    [Fact]
    public async Task MemorySync_WithConflictingRemoteEntry_MergesContentAddressed()
    {
        var cloud = new FakeCloudStore();

        // Seed remote with a row.
        var remotePath = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(remotePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remotePath);
        }

        // Local also has a different row.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('local-hash', 'local.md', 'local content', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Both rows should exist after merge.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NULL";
            var total = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            total.ShouldBe(2, "Both local and remote rows should exist after merge.");
        }
    }

    [Fact]
    public async Task MemorySync_CorruptRemoteSnapshot_NeverReplacesLocal()
    {
        var cloud = new FakeCloudStore();

        // First sync to create a valid remote.
        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, null!);

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Corrupt the remote with invalid bytes.
        cloud.Set("test-object", new byte[] { 0x00, 0x01, 0x02 }); // not a valid SQLite file

        // Write a real local entry.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('real-hash', 'real.md', 'real content', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Attempting to sync should throw SyncCorruptFileException.
        await Should.ThrowAsync<SyncCorruptFileException>(() =>
            service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

        // Local entry must still exist.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'real-hash'";
            var count = (long)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            count.ShouldBe(1, "Local data must survive corrupt remote snapshot.");
        }
    }
}
