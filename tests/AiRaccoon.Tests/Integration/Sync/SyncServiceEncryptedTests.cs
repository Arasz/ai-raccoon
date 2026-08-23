using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     Sync on an encrypted bank: VACUUM INTO snapshots of an encrypted bank are themselves
///     encrypted (docs/work/archive/2026-08-06-sqlite3mc-feature-surface.md F9), so every snapshot
///     access in the sync path must carry the bank key. Pins the full push + pull/merge round trip.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceEncryptedTests : IDisposable
{
    private const string Key = "sync-encrypted-test-key";
    private readonly string _bankPath;

    private readonly string _dataRoot;

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
            OpenSnapshotReadOnly(), TimeProvider.System, NullLogger<SyncService>.Instance);

        await InsertEntryAsync(_bankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Sent.ShouldBe(1);

        var remote = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        remote.ShouldNotBeNull();

        // The pushed bytes carry an embedded authenticity header (S2) ahead of the encrypted
        // snapshot itself — strip it before opening, same as the pull side does.
        new SyncBlobAuthenticator().TryUnwrap(remote.Data, out _, out var innerBytes).ShouldBeTrue(
            "a push against an encrypted bank must publish a wrapped blob carrying the embedded authenticity header");

        var pulledPath = Path.Combine(_dataRoot, "pulled.db");
        await File.WriteAllBytesAsync(pulledPath, innerBytes, TestContext.Current.CancellationToken);

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

        await InsertEntryAsync(_bankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(_bankPath, ct), OpenSnapshot(),
            OpenSnapshotReadOnly(), TimeProvider.System, NullLogger<SyncService>.Instance);

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
