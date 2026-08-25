using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Sync;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("sync-test");

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
                          CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS workspaces (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, agent_id TEXT NULL,
                              name TEXT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL, closed_at INTEGER NULL);
                          CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS sync_tombstones (project_id TEXT NOT NULL, hash TEXT NOT NULL, scope TEXT NOT NULL,
                              deleted_at INTEGER NOT NULL, PRIMARY KEY (project_id, hash, scope));
                          CREATE TABLE IF NOT EXISTS memory_source (
                              id INTEGER PRIMARY KEY,
                              source_type TEXT NOT NULL,
                              source_locator TEXT NOT NULL,
                              section TEXT NULL,
                              heading_path TEXT NULL);
                          CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source_identity
                              ON memory_source(source_type, source_locator, COALESCE(section, ''));
                          CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id);
                          """;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    [RetryFact]
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

    /// <summary>An unconfigured sync must fail before VACUUMing the local bank — no wasted local work for a call that is guaranteed to fail.</summary>
    [RetryFact]
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
    [RetryFact]
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

    [RetryFact]
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

        await using var conn = new SqliteConnection($"Data Source={BankPath}");
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_meta WHERE key = 'last_etag'";
        var etag = (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        etag.ShouldNotBeNullOrWhiteSpace();
    }

    [RetryFact]
    public async Task MemorySync_RemoteChanged_LocalEntriesNotLost()
    {
        var cloud = new FakeCloudStore();

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

        var bankBytes = await File.ReadAllBytesAsync(BankPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", bankBytes); // simulate same content to avoid merge

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

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

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

    [RetryFact]
    public async Task MemorySync_TombstonePropagation_NoResurrection()
    {
        var cloud = new FakeCloudStore();

        async Task<SqliteConnection> OpenBank(CancellationToken ct)
        {
            var conn = await CreateAndOpenAsync(BankPath, ct);
            return conn;
        }

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

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM entries WHERE hash = 'will-delete'";
            await del.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var tomb = conn.CreateCommand();
            tomb.CommandText = """
                               INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at)
                               VALUES ('acme', 'will-delete', 'project', 100)
                               """;
            await tomb.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

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
    [RetryFact]
    public async Task MemorySync_TombstoneFromRemote_DeletesAGroupMember_AndRecomputesSurvivorsContiguously()
    {
        var cloud = new FakeCloudStore();

        // Deliberately wrong chunk columns: proves the post-merge recompute actually ran, not
        // that the seed happened to already be correct.
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

        // Remote tombstone for the middle chunk, no entries of its own: the merge's
        // tombstone-apply path must remove h2 locally.
        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var remote = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var tomb = remote.CreateCommand();
            tomb.CommandText = "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('acme', 'h2', 'project', 1)";
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

    [RetryFact]
    public async Task MemorySync_TombstoneFromRemote_IsProjectScoped()
    {
        var cloud = new FakeCloudStore();

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                VALUES ('shared-hash', 'acme.md', 'acme content', 'project', 'acme', 1, 1),
                                       ('shared-hash', 'other.md', 'other content', 'project', 'other', 2, 2)
                                """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var remote = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var tomb = remote.CreateCommand();
            tomb.CommandText = """
                              INSERT INTO sync_tombstones (hash, scope, project_id, deleted_at)
                              VALUES ('shared-hash', 'project', 'acme', 1)
                              """;
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

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'shared-hash' AND project_id = 'acme'";
            var acmeCountScalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            acmeCountScalar.ShouldNotBeNull();
            ((long)acmeCountScalar).ShouldBe(0, "A tombstone for one project must not delete another project's same-hash row.");

            await using var otherCount = conn.CreateCommand();
            otherCount.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'shared-hash' AND project_id = 'other'";
            var otherScalar = await otherCount.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            otherScalar.ShouldNotBeNull();
            ((long)otherScalar).ShouldBe(1, "The same hash in a different project must survive a remote delete scoped to another project.");
        }
    }

    [RetryFact]
    public async Task MemorySync_WorkspaceRows_NotInSyncPayload()
    {
        var cloud = new FakeCloudStore();

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

    [RetryFact]
    public async Task MemorySync_MergedRows_Reindexed()
    {
        var cloud = new FakeCloudStore();

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

    [RetryFact]
    public async Task MemorySync_WorkspaceRowExcluded_Locally()
    {
        var cloud = new FakeCloudStore();

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

    [RetryFact]
    public async Task MemorySync_WithConflictingRemoteEntry_MergesContentAddressed()
    {
        var cloud = new FakeCloudStore();

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

    [RetryFact]
    public async Task MemorySync_CorruptRemoteSnapshot_NeverReplacesLocal()
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
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        cloud.Set("test-object", [0x00, 0x01, 0x02]); // not a valid SQLite file

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

        await Should.ThrowAsync<SyncCorruptFileException>(() =>
            service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

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

    // Settings (cloud credentials, embedding API key) must never leave the bank — stripped
    // from every pushed snapshot, not just workspace rows (ADR-0014).
    [RetryFact]
    public async Task MemorySync_SettingsRows_NotInSyncPayload()
    {
        var cloud = new FakeCloudStore();

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

    // Pulling/merging a snapshot with a stripped (empty) settings table must not error, and
    // must leave local settings untouched (ADR-0014).
    [RetryFact]
    public async Task MemorySync_MergeWithEmptySettingsRemote_SucceedsAndPreservesLocalSettings()
    {
        var cloud = new FakeCloudStore();

        // Remote snapshot as produced by a stripped push: settings table empty.
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

    // The merge branch must strip settings and workspace rows too, not just the first push
    // (ADR-0014).
    [RetryFact]
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

    // Same regression, conflict-retry path: a push that hits SyncConflictException re-merges
    // and re-VACUUMs into retryPath, which must be stripped too.
    [RetryFact]
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

    // Settings hold the sync credentials and embedding API key/base URL — a remote snapshot
    // must never overwrite them, whether from a hostile writer or a stale replica (ADR-0014).
    [RetryFact]
    public async Task MemorySync_PullWithHostileRemoteSettings_DoesNotOverwriteLocalSettings()
    {
        var cloud = new FakeCloudStore();

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
        // Pooling=False: otherwise Microsoft.Data.Sqlite keeps this connection's cached pages
        // alive in the pool past Dispose, masking the corruption the FileStream write below puts on disk.
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

    // A genuinely corrupt VACUUM INTO source fails VACUUM itself, so this substitutes a
    // pre-corrupted file for the merged-snapshot check via the same openReadOnly seam
    // SyncService already calls quick_check through.
    [RetryFact]
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
            // Identifies the merged snapshot by path (last one openSnapshot ran on, excluding
            // the local snapshot) rather than call position, so it stays correct if another openReadOnly call is added elsewhere.
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

    // Substitutes the corrupt file only at the retry branch's integrity check, so the first
    // (valid) merge attempt still runs far enough to hit the forced conflict.
    [RetryFact]
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
            // Same path-identity rule as above, further narrowed to only the check after a
            // conflict is raised — otherwise it would also match the first (valid) merge attempt's check, and the retry branch would never be reached.
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

    [RetryFact]
    public async Task MemorySync_MergedRows_CarrySourceFileAndSectionAndAreCorrectlyGrouped()
    {
        var cloud = new FakeCloudStore();

        // Two chunks of the same remote source file, sharing a (ctx, source_file) group.
        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var conn = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, updated_at)
                                 VALUES ('r1', 'doc.md#0', 'chunk one', 'doc.md', 'Intro', 'project', 'acme', 1, 1),
                                        ('r2', 'doc.md#1', 'chunk two', 'doc.md', 'Body', 'project', 'acme', 2, 2)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
            "SELECT hash, source_file, section, chunk_index, total_chunks FROM entries WHERE source_file = 'doc.md' ORDER BY id";
        var rows = new List<(string Hash, string? SourceFile, string? Section, long ChunkIndex, long TotalChunks)>();
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        rows.Select(r => r.Hash).ShouldBe(["r1", "r2"]);
        rows.Select(r => r.SourceFile).ShouldBe(["doc.md", "doc.md"],
            "a pulled row must keep its source_file, or chunk grouping and the FTS source-weight can never apply to it.");
        rows.Select(r => r.Section).ShouldBe(["Intro", "Body"],
            "a pulled row must keep its section, or it search-ranks as body-weight only.");
        rows.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0L, 2L), (1L, 2L)],
            "pulled rows sharing a source_file must be grouped by the post-merge chunk-column recompute.");
    }

    [RetryFact]
    public async Task MemorySync_RemoteSnapshotNewerSchemaVersion_RefusedBeforeMerging()
    {
        var cloud = new FakeCloudStore();

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

                await using var stamp = conn.CreateCommand();
                stamp.CommandText = "PRAGMA user_version = 99";
                await stamp.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(remoteSeedPath);
        }

        // Local also has a row of its own, to prove the merge never runs at all.
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

        await Should.ThrowAsync<UnsupportedSchemaVersionException>(() =>
            service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken));

        await using (var conn = new SqliteConnection($"Data Source={BankPath}"))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'remote-hash'";
            var countScalar = await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            countScalar.ShouldNotBeNull();
            ((long)countScalar).ShouldBe(0, "a forward-version remote snapshot must never be merged.");
        }
    }

    [RetryFact]
    public async Task MergeAsync_SetsSourceId_OnMergedEntries()
    {
        var cloud = new FakeCloudStore();

        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var conn = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, updated_at)
                                 VALUES ('r1', 'doc.md#0', 'chunk one', 'doc.md', 'Intro', 'project', 'acme', 1, 1),
                                        ('r2', 'doc.md#1', 'chunk two', 'doc.md', 'Body', 'project', 'acme', 2, 2),
                                        ('r3', 'note.md', 'manual note', NULL, NULL, 'project', 'acme', 3, 3)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
            "SELECT hash, source_id, source_file FROM entries WHERE workspace_id IS NULL ORDER BY hash";
        var rows = new List<(string Hash, long? SourceId, string? SourceFile)>();
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        rows.ShouldAllBe(r => r.SourceId != null && r.SourceId > 0,
            "every merged entry must have source_id resolved");

        var fileSources = rows.Where(r => r.SourceFile == "doc.md").ToList();
        fileSources.ShouldAllBe(r => r.SourceId > 0);

        var manualSources = rows.Where(r => r.SourceFile is null).ToList();
        manualSources.ShouldAllBe(r => r.SourceId > 0);

        await using var srcCount = check.CreateCommand();
        srcCount.CommandText = "SELECT COUNT(*) FROM memory_source";
        var count = (long)(await srcCount.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        count.ShouldBeGreaterThanOrEqualTo(2, "at least file and manual sources must exist");
    }

    [RetryFact]
    public async Task MergeAsync_PreservesSourceFile_Section_ForFTS()
    {
        var cloud = new FakeCloudStore();

        var remotePath = Path.Combine(_dataRoot, "remote.db");
        await using (var conn = await CreateAndOpenAsync(remotePath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, created_at, updated_at)
                                 VALUES ('r1', 'doc.md#0', 'chunk one', 'doc.md', 'Intro', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
        select.CommandText = "SELECT source_file, section FROM entries WHERE hash = 'r1'";
        await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        reader.GetString(0).ShouldBe("doc.md",
            "source_file must survive the merge for FTS backing");
        reader.GetString(1).ShouldBe("Intro",
            "section must survive the merge for FTS backing");
    }

    [RetryFact]
    public async Task MemorySync_ResolvesTheCloudStorePerCall()
    {
        // The resolver runs inside each sync cycle, so `sync add/remove` settings writes take
        // effect without a restart — two calls resolve twice.
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

    /// <summary>Forces exactly one SyncConflictException on the first push, then delegates.</summary>
    private sealed class ConflictOnceCloudStore(FakeCloudStore inner) : ICloudStore
    {
        public bool ConflictWasRaised { get; private set; }

        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) => inner.PullAsync(objectKey, cancellationToken);

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
}
