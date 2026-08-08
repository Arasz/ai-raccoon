using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sync;

/// <summary>
///     Sync on an encrypted bank (upgrade doc docs/work/2026-08-06-sqlite3mc-2.4.0-upgrade.md in-flight
///     item "sync checkpoint on an encrypted bank"). VACUUM INTO snapshots of an encrypted bank are
///     themselves encrypted (measured docs/work/2026-08-06-sqlite3mc-feature-surface.md F9), so every
///     snapshot access in the sync path must carry the bank key — local strip, quick_check, and the
///     merge ATTACH. These tests pin the full encrypted push + pull/merge round trip.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceEncryptedTests : IDisposable
{
    private const string Key = "sync-encrypted-test-key";

    private readonly string _dataRoot;
    private readonly string _bankPath;

    public SyncServiceEncryptedTests()
    {
        _dataRoot = TestData.CreateTempRoot("sync-encrypted");
        _bankPath = Path.Combine(_dataRoot, "memory.db");
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static async Task<SqliteConnection> CreateAndOpenAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={path};Password={Key}");
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

    /// <summary>Mirrors the DI openSnapshot after the encrypted-sync fix: key + vec0 loaded,
    /// read-write (the strip DELETEs + VACUUMs the snapshot).</summary>
    private Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshot() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Password={Key}");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    /// <summary>Mirrors the DI openReadOnly: key + vec0 loaded, read-only (quick_check only).</summary>
    private Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshotReadOnly() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Password={Key};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    private static async Task InsertEntryAsync(string path, string hash, string entryPath, string value,
        CancellationToken ct)
    {
        await using var conn = await CreateAndOpenAsync(path, ct);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
                             INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                             VALUES ($hash, $path, $value, 'project', 'acme', 1, 1)
                             """;
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$path", entryPath);
        insert.Parameters.AddWithValue("$value", value);
        await insert.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task MemorySync_EncryptedBank_PushesEncryptedSnapshot()
    {
        var cloud = new FakeCloudStore();
        var service = new SyncService(cloud, ct => CreateAndOpenAsync(_bankPath, ct), OpenSnapshot(),
            OpenSnapshotReadOnly(), TimeProvider.System, null!);

        await InsertEntryAsync(_bankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Sent.ShouldBe(1);

        var remote = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        remote.ShouldNotBeNull();

        // The pushed snapshot is encrypted: unreadable without the key, readable with it.
        var pulledPath = Path.Combine(_dataRoot, "pulled.db");
        await File.WriteAllBytesAsync(pulledPath, remote.Data, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var c = new SqliteConnection($"Data Source={pulledPath}");
            await c.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM entries";
            await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        });

        await using (var c = new SqliteConnection($"Data Source={pulledPath};Password={Key}"))
        {
            await c.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM entries";
            (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1L);
        }
    }

    [Fact]
    public async Task MemorySync_EncryptedBank_MergesEncryptedRemote()
    {
        var cloud = new FakeCloudStore();

        // Seed the remote with an encrypted snapshot from a second bank holding a different entry.
        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);
        var remoteSnapshotPath = Path.Combine(_dataRoot, "remote-snapshot.db");
        await using (var conn = await CreateAndOpenAsync(remoteBankPath, TestContext.Current.CancellationToken))
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{remoteSnapshotPath}'";
            await vac.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        cloud.Set("obj", await File.ReadAllBytesAsync(remoteSnapshotPath, TestContext.Current.CancellationToken));

        // Local encrypted bank holds its own entry; sync must pull + merge the encrypted remote.
        await InsertEntryAsync(_bankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(_bankPath, ct), OpenSnapshot(),
            OpenSnapshotReadOnly(), TimeProvider.System, null!);

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Received.ShouldBe(1);

        await using (var conn = await CreateAndOpenAsync(_bankPath, TestContext.Current.CancellationToken))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM entries";
            (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2L);
        }
    }
}
