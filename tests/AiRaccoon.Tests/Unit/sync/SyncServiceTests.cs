using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Embedding;
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
                              source_file TEXT NULL,
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
                              total_chunks INTEGER NOT NULL DEFAULT 0
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

    /// <summary>An unconfigured sync must fail before VACUUMing the local bank — no wasted local work for a call that is guaranteed to fail (see SyncTools thinning, PR body).</summary>
    [Fact]
    public async Task MemorySync_WithoutConfiguredCloudStore_FailsBeforeTouchingTheLocalBank()
    {
        var openBankCalls = 0;
        var service = new SyncService(new NullCloudStore(),
            ct =>
            {
                openBankCalls++;
                return CreateAndOpenAsync(BankPath, ct);
            },
            OpenSnapshotAsync,
            async (path, ct) =>
            {
                var c = new SqliteConnection($"Data Source={path}");
                await c.OpenAsync(ct);
                return c;
            }, TimeProvider.System, NullLogger<SyncService>.Instance);

        await Should.ThrowAsync<SyncNotConfiguredException>(() =>
            service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

        openBankCalls.ShouldBe(0);
    }

    /// <summary>The default objectKey is the service's concern, not the caller's — matches the memory-{projectId}.db convention when the caller passes none.</summary>
    [Fact]
    public async Task MemorySync_WithNoObjectKey_DefaultsToMemoryDashProjectId()
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

        await service.MemorySyncAsync("acme", cancellationToken: TestContext.Current.CancellationToken);

        var stored = await cloud.PullAsync("memory-acme.db", TestContext.Current.CancellationToken);
        stored.ShouldNotBeNull("the default object key memory-acme.db must be the one actually pushed to");
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

    /// <summary>docs/plans/2026-08-08-search-knn-perf.md §3.3: the merge's tombstone-apply step can remove a group member; the bank-wide recompute after the merge must leave survivors contiguous.</summary>
    [Fact]
    public async Task MemorySync_TombstoneFromRemote_DeletesAGroupMember_AndRecomputesSurvivorsContiguously()
    {
        var cloud = new FakeCloudStore();

        // Seed local with three chunks of one source file, deliberately wrong chunk columns —
        // passing this test proves the post-merge recompute actually ran, not that the seed
        // happened to already be correct.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, chunk_index, total_chunks)
                                 VALUES ('h1', 'doc.md', 'chunk one', 'doc.md', 'project', 'acme', 1, 1, 9, 9),
                                        ('h2', 'doc.md', 'chunk two', 'doc.md', 'project', 'acme', 2, 2, 9, 9),
                                        ('h3', 'doc.md', 'chunk three', 'doc.md', 'project', 'acme', 3, 3, 9, 9)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // A remote snapshot carrying a tombstone for the middle chunk but no entries of its own
        // — the merge's tombstone-apply path is what must remove h2 locally.
        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var remote = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var tomb = remote.CreateCommand();
            tomb.CommandText = "INSERT INTO sync_tombstones (hash, scope, deleted_at) VALUES ('h2', 'project', 1)";
            await tomb.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        cloud.Set("test-object", await File.ReadAllBytesAsync(remotePath, TestContext.Current.CancellationToken));

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

        await using var check = new SqliteConnection($"Data Source={BankPath}");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var select = check.CreateCommand();
        select.CommandText =
            "SELECT hash, chunk_index, total_chunks FROM entries WHERE source_file = 'doc.md' ORDER BY id";
        var survivors = new List<(string Hash, long ChunkIndex, long TotalChunks)>();
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                survivors.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        survivors.Select(s => s.Hash).ShouldBe(["h1", "h3"]);
        survivors.Select(s => (s.ChunkIndex, s.TotalChunks)).ShouldBe([(0L, 2L), (1L, 2L)]);
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

    //
    // Security regression: the settings/workspace strip only ran on the FIRST push
    // (remote is null). When a remote object already exists the merge branch re-VACUUMs the
    // live bank into mergedPath and pushes THAT unstripped — leaking cloud credentials and
    // workspace-scoped entries into the very bucket those credentials unlock.
    //
    [Fact]
    public async Task MemorySync_MergeBranchWithExistingRemote_StripsSettingsAndWorkspaceFromPushedPayload()
    {
        var cloud = new FakeCloudStore();

        // Seed an existing remote object so `remote is not null` and the merge branch runs.
        var remoteSeedPath = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteSeedPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        // Local bank carries a fake secret in settings and a workspace-scoped entry.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var settings = conn.CreateCommand();
            settings.CommandText = $"""
                                    INSERT INTO settings (key, value) VALUES ('{SyncSettingsKeys.SecretKey}', 'super-secret-value')
                                    """;
            await settings.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

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

        // Inspect what actually got pushed to the cloud.
        var cloudObj = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        cloudObj.ShouldNotBeNull();
        var pushedPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(pushedPath, cloudObj.Data, TestContext.Current.CancellationToken);
            await using var pushedConn = new SqliteConnection($"Data Source={pushedPath}");
            await pushedConn.OpenAsync(TestContext.Current.CancellationToken);

            await using var settingsCount = pushedConn.CreateCommand();
            settingsCount.CommandText = "SELECT COUNT(*) FROM settings";
            var settingsScalar = await settingsCount.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            settingsScalar.ShouldNotBeNull();
            ((long)settingsScalar).ShouldBe(0,
                "The merge branch must strip settings from the pushed payload, not just the first push.");

            await using var workspaceCount = pushedConn.CreateCommand();
            workspaceCount.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NOT NULL";
            var workspaceScalar = await workspaceCount.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            workspaceScalar.ShouldNotBeNull();
            ((long)workspaceScalar).ShouldBe(0,
                "The merge branch must strip workspace entries from the pushed payload, not just the first push.");
        }
        finally
        {
            File.Delete(pushedPath);
        }
    }

    //
    // Same regression as above, but for the conflict-retry path: a push that hits
    // SyncConflictException re-merges and re-VACUUMs into retryPath, which must be stripped too.
    //
    [Fact]
    public async Task MemorySync_ConflictRetryBranch_StripsSettingsAndWorkspaceFromPushedPayload()
    {
        var inner = new FakeCloudStore();
        var cloud = new ConflictOnceCloudStore(inner);

        // Seed an existing remote object so both the initial pull and the post-conflict
        // re-pull see `remote is not null`.
        var remoteSeedPath = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteSeedPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            inner.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        // Local bank carries a fake secret in settings and a workspace-scoped entry.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var settings = conn.CreateCommand();
            settings.CommandText = $"""
                                    INSERT INTO settings (key, value) VALUES ('{SyncSettingsKeys.SecretKey}', 'super-secret-value')
                                    """;
            await settings.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

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

        cloud.ConflictWasRaised.ShouldBeTrue("The test must actually exercise the conflict-retry branch.");

        // Inspect what actually got pushed to the cloud on the retry attempt.
        var cloudObj = await inner.PullAsync("test-object", TestContext.Current.CancellationToken);
        cloudObj.ShouldNotBeNull();
        var pushedPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(pushedPath, cloudObj.Data, TestContext.Current.CancellationToken);
            await using var pushedConn = new SqliteConnection($"Data Source={pushedPath}");
            await pushedConn.OpenAsync(TestContext.Current.CancellationToken);

            await using var settingsCount = pushedConn.CreateCommand();
            settingsCount.CommandText = "SELECT COUNT(*) FROM settings";
            var settingsScalar = await settingsCount.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            settingsScalar.ShouldNotBeNull();
            ((long)settingsScalar).ShouldBe(0,
                "The conflict-retry branch must strip settings from the pushed payload.");

            await using var workspaceCount = pushedConn.CreateCommand();
            workspaceCount.CommandText = "SELECT COUNT(*) FROM entries WHERE workspace_id IS NOT NULL";
            var workspaceScalar = await workspaceCount.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            workspaceScalar.ShouldNotBeNull();
            ((long)workspaceScalar).ShouldBe(0,
                "The conflict-retry branch must strip workspace entries from the pushed payload.");
        }
        finally
        {
            File.Delete(pushedPath);
        }
    }

    /// <summary>Forces exactly one SyncConflictException on the first push, then delegates.</summary>
    private sealed class ConflictOnceCloudStore(FakeCloudStore inner) : ICloudStore
    {
        public bool ConflictWasRaised { get; private set; }

        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default)
            => inner.PullAsync(objectKey, cancellationToken);

        public Task<string> PushAsync(string objectKey, byte[] data, string? etag,
            CancellationToken cancellationToken = default)
        {
            if (!ConflictWasRaised)
            {
                ConflictWasRaised = true;
                throw new SyncConflictException("Simulated concurrent write.");
            }

            return inner.PushAsync(objectKey, data, etag, cancellationToken);
        }
    }

    //
    // Issue #121: pull unconditionally clobbers local settings from the remote snapshot.
    // Settings hold the sync credentials and the embedding API key/base URL — a remote
    // snapshot must never be allowed to overwrite them, whether the source is a hostile
    // writer to the shared object store or a stale pre-#88 replica.
    //
    [Fact]
    public async Task MemorySync_PullWithHostileRemoteSettings_DoesNotOverwriteLocalSettings()
    {
        var cloud = new FakeCloudStore();

        // Remote snapshot carries a hostile embedding.baseUrl pointing at an attacker host.
        var remoteSeedPath = Path.GetTempFileName();
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteSeedPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = $"""
                                     INSERT INTO settings (key, value) VALUES ('{EmbeddingSettingsKeys.BaseUrl}', 'http://attacker.example.invalid')
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        // Local bank trusts its own embedding endpoint.
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = $"""
                                  INSERT INTO settings (key, value) VALUES ('{EmbeddingSettingsKeys.BaseUrl}', 'https://api.trusted-embeddings.example')
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

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = $"SELECT value FROM settings WHERE key = '{EmbeddingSettingsKeys.BaseUrl}'";
            var value = (string?)await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            value.ShouldBe("https://api.trusted-embeddings.example",
                "A remote settings row must never overwrite the local machine's settings.");
        }
    }

    /// <summary>Builds a valid SQLite file with page 10 overwritten by garbage: the file still opens, but PRAGMA quick_check reports damage instead of throwing.</summary>
    private static async Task<string> CreateCorruptSnapshotAsync(CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        // Pooling=False: without it, Microsoft.Data.Sqlite keeps this connection's native
        // handle (and its in-memory page cache) alive in the pool after Dispose, so a later
        // connection on the same path can read stale cached pages instead of the corrupted
        // bytes the FileStream write below puts on disk.
        await using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await conn.OpenAsync(ct);
            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA page_size = 4096";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await using (var create = conn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE filler (id INTEGER PRIMARY KEY, value TEXT)";
                await create.ExecuteNonQueryAsync(ct);
            }

            for (var i = 0; i < 800; i++)
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO filler (value) VALUES (@v)";
                insert.Parameters.AddWithValue("@v", new string('x', 200) + i);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(9 * 4096, SeekOrigin.Begin);
            var garbage = new byte[4096];
            Array.Fill(garbage, (byte)0xFF);
            await fs.WriteAsync(garbage, ct);
        }

        // Prove the precondition the two corruption tests rely on, instead of trusting it by hand.
        await using (var verify = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await verify.OpenAsync(ct);
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = (string)(await check.ExecuteScalarAsync(ct))!;
            result.ShouldNotBe("ok", "the corrupt fixture must actually fail its own integrity check");
        }

        return path;
    }

    //
    // Issue #115: quick_check never runs on the bytes actually pushed by the merge branch.
    // A genuinely corrupt VACUUM INTO source fails VACUUM itself, so this substitutes a
    // pre-corrupted file for the merged-snapshot check specifically — the same openReadOnly
    // seam SyncService already calls quick_check through.
    //
    [Fact]
    public async Task MemorySync_MergeBranchWithExistingRemote_CorruptMergedSnapshot_RejectedBeforeUpload()
    {
        var cloud = new FakeCloudStore();

        var remoteSeedPath = Path.GetTempFileName();
        byte[] remoteSeedBytes;
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteSeedPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            remoteSeedBytes = await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken);
            cloud.Set("test-object", remoteSeedBytes);
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('local-hash', 'local.md', 'local content', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var corruptPath = await CreateCorruptSnapshotAsync(TestContext.Current.CancellationToken);
        try
        {
            // The merged snapshot is identified by path, not by call position: it is whatever
            // path StripNonSyncableAsync (the openSnapshot seam) most recently ran on, as long
            // as that path isn't the first one it ran on (the local snapshot, always stripped
            // first). That stays correct even if a new openReadOnly call is inserted anywhere
            // else in the flow.
            string? localSnapshotPath = null;
            string? lastSnapshotPath = null;

            async Task<SqliteConnection> OpenSnapshot(string path, CancellationToken ct)
            {
                localSnapshotPath ??= path;
                lastSnapshotPath = path;
                return await OpenSnapshotAsync(path, ct);
            }

            async Task<SqliteConnection> OpenReadOnly(string path, CancellationToken ct)
            {
                var isMergedSnapshotCheck = path == lastSnapshotPath && path != localSnapshotPath;
                var actualPath = isMergedSnapshotCheck ? corruptPath : path;
                var c = new SqliteConnection($"Data Source={actualPath}");
                await c.OpenAsync(ct);
                return c;
            }

            var service = new SyncService(cloud,
                ct => CreateAndOpenAsync(BankPath, ct),
                OpenSnapshot,
                OpenReadOnly,
                TimeProvider.System, NullLogger<SyncService>.Instance);

            await Should.ThrowAsync<SyncCorruptFileException>(() =>
                service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

            var cloudObj = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
            cloudObj.ShouldNotBeNull();
            cloudObj.Data.ShouldBe(remoteSeedBytes,
                "A merged snapshot that fails its integrity check must never be pushed.");
        }
        finally
        {
            File.Delete(corruptPath);
        }
    }

    //
    // Same regression as above, but for the conflict-retry path: the corrupt file is
    // substituted only at the retry branch's own integrity-check call, so the first
    // (valid) merge attempt still runs far enough to hit the forced conflict.
    //
    [Fact]
    public async Task MemorySync_ConflictRetryBranch_CorruptRetrySnapshot_RejectedBeforeUpload()
    {
        var inner = new FakeCloudStore();
        var cloud = new ConflictOnceCloudStore(inner);

        var remoteSeedPath = Path.GetTempFileName();
        byte[] remoteSeedBytes;
        try
        {
            await using (var conn = await CreateAndOpenAsync(remoteSeedPath, TestContext.Current.CancellationToken))
            {
                await using var insert = conn.CreateCommand();
                insert.CommandText = """
                                     INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                     VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                                     """;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            remoteSeedBytes = await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken);
            inner.Set("test-object", remoteSeedBytes);
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('local-hash', 'local.md', 'local content', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var corruptPath = await CreateCorruptSnapshotAsync(TestContext.Current.CancellationToken);
        try
        {
            // Same path-identity rule as the single-merge test above (last openSnapshot path,
            // excluding the local snapshot), further narrowed to only the check that runs
            // after a conflict was actually raised. Without that narrowing this would also
            // match the first (valid) merge attempt's own check, which must stay valid or the
            // conflict/retry branch is never reached.
            string? localSnapshotPath = null;
            string? lastSnapshotPath = null;

            async Task<SqliteConnection> OpenSnapshot(string path, CancellationToken ct)
            {
                localSnapshotPath ??= path;
                lastSnapshotPath = path;
                return await OpenSnapshotAsync(path, ct);
            }

            async Task<SqliteConnection> OpenReadOnly(string path, CancellationToken ct)
            {
                var isMergedSnapshotCheck = path == lastSnapshotPath && path != localSnapshotPath;
                var actualPath = isMergedSnapshotCheck && cloud.ConflictWasRaised ? corruptPath : path;
                var c = new SqliteConnection($"Data Source={actualPath}");
                await c.OpenAsync(ct);
                return c;
            }

            var service = new SyncService(cloud,
                ct => CreateAndOpenAsync(BankPath, ct),
                OpenSnapshot,
                OpenReadOnly,
                TimeProvider.System, NullLogger<SyncService>.Instance);

            await Should.ThrowAsync<SyncCorruptFileException>(() =>
                service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

            cloud.ConflictWasRaised.ShouldBeTrue("The test must actually exercise the conflict-retry branch.");

            var cloudObj = await inner.PullAsync("test-object", TestContext.Current.CancellationToken);
            cloudObj.ShouldNotBeNull();
            cloudObj.Data.ShouldBe(remoteSeedBytes,
                "A corrupt retry-branch snapshot must never be pushed.");
        }
        finally
        {
            File.Delete(corruptPath);
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
