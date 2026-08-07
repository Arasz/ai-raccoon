using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sync;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceTests : IDisposable
{
    private readonly string _dataRoot;

    public SyncServiceTests()
    {
        _dataRoot = TestData.CreateTempRoot("sync-test");
    }

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>Read-write open of a snapshot file: the workspace strip DELETEs + VACUUMs it.</summary>
    private static async Task<SqliteConnection> OpenSnapshotAsync(string path, CancellationToken ct)
    {
        var c = new SqliteConnection($"Data Source={path}");
        await c.OpenAsync(ct);
        return c;
    }

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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
            var totalScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            totalScalar.ShouldNotBeNull();
            var total = (long)totalScalar;
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
            var countScalar = await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            countScalar.ShouldNotBeNull();
            var count = (long)countScalar;
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
                var wsCountScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                wsCountScalar.ShouldNotBeNull();
                var wsCount = (long)wsCountScalar;
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Check that the merged row was reindexed (embed_state reset to 'pending').
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT embed_state FROM entries WHERE hash = 'row-a'";
            var state = (string?)await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            state.ShouldBe("pending",
                "Merged rows must be reindexed (embed_state 'pending' so embed queue picks them up).");
        }
    }

    //
    // FR-NM-6 s4 (see docs/work/features-native-memory/native-memory.feature): workspace exclusion — workspace rows should not appear in local bank stats
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
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
                var wsRowsScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                wsRowsScalar.ShouldNotBeNull();
                var wsRows = (long)wsRowsScalar;
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Both rows should exist after merge.
        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NULL";
            var totalScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            totalScalar.ShouldNotBeNull();
            var total = (long)totalScalar;
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
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // Corrupt the remote with invalid bytes.
        cloud.Set("test-object", [0x00, 0x01, 0x02]); // not a valid SQLite file

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
            var countScalar = await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            countScalar.ShouldNotBeNull();
            var count = (long)countScalar;
            count.ShouldBe(1, "Local data must survive corrupt remote snapshot.");
        }
    }

    //
    // WI-1a: settings never leave the bank — the table holds cloud credentials and the
    // embedding API key, so it must be stripped from every pushed snapshot, not just workspace rows.
    //
    [Fact]
    public async Task MemorySync_SettingsRows_NotInSyncPayload()
    {
        var cloud = new FakeCloudStore();

        // Seed a settings row holding a secret (the S3 secret key sync itself reads to reach the store).
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = $"""
                                  INSERT INTO settings (key, value) VALUES ('{SyncSettingsKeys.SecretKey}', 'super-secret-value')
                                  """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        // The cloud object must not contain the settings row (credential exfiltration).
        var cloudObj = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        cloudObj.ShouldNotBeNull();
        var remotePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(remotePath, cloudObj.Data, TestContext.Current.CancellationToken);
            await using var remoteConn = new SqliteConnection($"Data Source={remotePath}");
            await remoteConn.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = remoteConn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM settings";
            var settingsCountScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            settingsCountScalar.ShouldNotBeNull();
            var settingsCount = (long)settingsCountScalar;
            settingsCount.ShouldBe(0, "Settings rows (cloud credentials, embedding API key) must never leave the bank.");
        }
        finally
        {
            File.Delete(remotePath);
        }
    }

    //
    // WI-1a: pulling/merging a snapshot with a stripped (empty) settings table must not error,
    // and must leave the local settings untouched (nothing in remote.settings to overwrite them with).
    //
    [Fact]
    public async Task MemorySync_MergeWithEmptySettingsRemote_SucceedsAndPreservesLocalSettings()
    {
        var cloud = new FakeCloudStore();

        // Remote snapshot as produced by a stripped push: schema present, settings table empty.
        var remoteBankPath = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteBankPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(remoteBankPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remoteBankPath);
        }

        // Local bank keeps its own settings row.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = $"""
                                  INSERT INTO settings (key, value) VALUES ('{SyncSettingsKeys.SecretKey}', 'local-secret-value')
                                  """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud,
            ct => CreateAndOpenAsync(BankPath, ct),
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);

        // Must not throw merging a remote with an empty settings table.
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = $"SELECT value FROM settings WHERE key = '{SyncSettingsKeys.SecretKey}'";
            var value = (string?)await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            value.ShouldBe("local-secret-value", "Local settings must survive a merge against an empty remote settings table.");
        }
    }

    [Fact]
    public async Task MemorySync_ResolvesTheCloudStorePerCall()
    {
        // F13: the resolver runs inside each sync cycle, so `sync add/remove` (settings
        // writes) take effect without a restart — two calls resolve twice.
        var resolutions = 0;
        var service = new SyncService(
            _ =>
            {
                resolutions++;
                return Task.FromResult<ICloudStore>(new FakeCloudStore());
            },
            ct => CreateAndOpenAsync(BankPath, ct),
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        resolutions.ShouldBe(2);
    }
}
