using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     WP7-T04 (docs/work/2026-08-21-code-search-implementation-plan.md §3.7, §12.2 H6): the code
///     corpus (<c>code_entries</c>/<c>code_fts</c>/<c>vec_code</c>) must never leave the machine
///     via cloud sync. <c>StripNonSyncableAsync</c> DROPs those tables — not row-deletion — from
///     every pushed snapshot on all three push paths (local, merged, retry-merged,
///     <c>SyncService.cs:74,105,161</c>). Pull/merge never names the code tables, so a pull must
///     leave the local code corpus untouched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SyncServiceCodeExclusionTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("sync-code-exclusion");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>Full production schema (incl. code_entries/code_fts/vec_code) via the real DDL —
    /// avoids hand-rolling a second copy of the code corpus shape that could drift from MemorySchema.</summary>
    private static async Task<SqliteConnection> CreateAndOpenAsync(string path, CancellationToken ct)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        conn.EnableExtensions();
        conn.LoadVector();
        await MemorySchema.EnsureAsync(conn, ct);
        return conn;
    }

    /// <summary>Mirrors the DI openSnapshot (AppRegistrations.OpenSnapshotWithKey): vec0 loaded,
    /// read-write (the strip DROPs + VACUUMs the snapshot).</summary>
    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshot() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path}");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    /// <summary>Mirrors the DI openReadOnly: vec0 loaded, read-only (quick_check only).</summary>
    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenReadOnly() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    private static async Task SeedCodeRowAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
                             INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                             VALUES (1, 'code-hash-1', 'src/foo.cs', 'public sealed class Foo { }', 'src/foo.cs', 1, 1, 'acme', 1, 1)
                             """;
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedRemoteEntryAsync(string remoteSeedPath, CancellationToken ct)
    {
        await using var remoteConn = await CreateAndOpenAsync(remoteSeedPath, ct);
        await using var insert = remoteConn.CreateCommand();
        insert.CommandText = """
                             INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                             VALUES ('remote-hash', 'remote.md', 'remote content', 'project', 'acme', 1, 1)
                             """;
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string>> TableNamesAsync(string dbPath, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void AssertNoCodeTables(List<string> tables)
    {
        tables.ShouldNotContain("code_entries", "the code corpus must never leave the machine via sync (§3.7).");
        tables.ShouldNotContain("code_fts");
        tables.ShouldNotContain("vec_code");
        tables.Any(t => t.StartsWith("vec_code_", StringComparison.Ordinal)).ShouldBeFalse(
            "vec0 shadow tables for vec_code must not survive the strip either.");
        tables.Any(t => t.StartsWith("code_fts_", StringComparison.Ordinal)).ShouldBeFalse(
            "fts5 shadow tables for code_fts must not survive the strip either.");
    }

    // --- Push path 1: local snapshot (SyncService.cs ~:74) -------------------------------------

    [Fact]
    public async Task Sync_LocalPush_DropsCodeTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-local.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoCodeTables(await TableNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    // --- Push path 2: merged snapshot (SyncService.cs ~:105) -----------------------------------

    [Fact]
    public async Task Sync_MergedPush_DropsCodeTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);
        result.Received.ShouldBe(1, "the merge branch must actually run for this test to exercise the merged-push strip.");

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-merged.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoCodeTables(await TableNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    // --- Push path 3: retry-merged snapshot (SyncService.cs ~:161) -----------------------------

    [Fact]
    public async Task Sync_RetryMergedPush_DropsCodeTablesFromSnapshot()
    {
        var inner = new FakeCloudStore();
        var cloud = new ConflictOnceCloudStore(inner);

        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        inner.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        cloud.ConflictWasRaised.ShouldBeTrue("the test must actually exercise the conflict-retry (third) push path.");

        var pulled = await inner.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-retry.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoCodeTables(await TableNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    // --- Pull side: merge never names code tables, so a pull must not touch the local corpus ---

    [Fact]
    public async Task Sync_Pull_LeavesLocalCodeCorpusUntouched()
    {
        var cloud = new FakeCloudStore();
        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        await using var check = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken);
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM code_entries WHERE id = 1";
        var scalar = await count.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        ((long)scalar!).ShouldBe(1L, "a pull/merge must never touch the local code corpus — it names entries/sync_tombstones only.");
    }

    // --- Backward compat: a pre-code-corpus snapshot has none of these tables ------------------

    /// <summary>
    ///     H6: snapshots are opened via <c>openSnapshot</c>, which never runs
    ///     <c>MemorySchema.EnsureAsync</c> — so the strip must tolerate a snapshot that predates
    ///     the code corpus (a bare <c>DROP TABLE</c> without <c>IF EXISTS</c> would abort the
    ///     push). Uses the legacy hand-rolled schema (no code tables at all), matching the shape
    ///     of a bank from before this feature.
    /// </summary>
    [Fact]
    public async Task Sync_PreCodeCorpusSnapshot_StripsWithoutError()
    {
        var cloud = new FakeCloudStore();

        await using (var conn = await CreateLegacyBankAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                 VALUES ('h1', 'p1.md', 'v1', 'project', 'acme', 1, 1)
                                 """;
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateLegacyBankAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);

        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        result.Sent.ShouldBe(1,
            "a pre-code-corpus bank must still sync successfully — DROP TABLE IF EXISTS must not throw when the code tables never existed.");
    }

    /// <summary>A hand-rolled bank shape with no code_entries/code_fts/vec_code at all — the
    /// pre-feature legacy shape (mirrors SyncServiceTests.CreateAndOpenAsync).</summary>
    private static async Task<SqliteConnection> CreateLegacyBankAsync(string path, CancellationToken ct)
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
                          CREATE TABLE IF NOT EXISTS sync_tombstones (hash TEXT NOT NULL, scope TEXT NOT NULL,
                              deleted_at INTEGER NOT NULL, PRIMARY KEY (hash, scope));
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

    /// <summary>Forces exactly one SyncConflictException on the first push, then delegates (mirrors
    /// SyncServiceTests.ConflictOnceCloudStore — duplicated locally to keep this file self-contained).</summary>
    private sealed class ConflictOnceCloudStore(FakeCloudStore inner) : ICloudStore
    {
        public bool ConflictWasRaised { get; private set; }

        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) =>
            inner.PullAsync(objectKey, cancellationToken);

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
