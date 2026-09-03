using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

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

    /// <summary>Seeds a code row and drives it through embed_state='embedded' with a real 768-float
    /// BLOB, so the vec_code_au trigger fires and populates vec0's shadow tables (chunks/rowids/etc)
    /// — the shape the strip's DROP TABLE IF EXISTS vec_code must actually tear down, not just an
    /// empty pending row (integration review S9).</summary>
    private static async Task SeedEmbeddedCodeRowAsync(SqliteConnection conn, CancellationToken ct)
    {
        await SeedCodeRowAsync(conn, ct);

        var vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray());
        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1";
        update.Parameters.AddWithValue("@embedding", vector);
        await update.ExecuteNonQueryAsync(ct);

        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT count(*) FROM vec_code WHERE rowid = 1";
        var vecCount = (long)(await count.ExecuteScalarAsync(ct))!;
        vecCount.ShouldBe(1L, "the vec_code_au trigger must have populated vec_code before the push runs, " +
                              "or this test proves nothing about a populated corpus.");
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

    private static void AssertNoMachineLocalTables(List<string> tables)
    {
        tables.ShouldNotContain("workspaces");
        tables.ShouldNotContain("promotion_queue");
        tables.ShouldNotContain("promotion_queue_prune_requests");
    }

    // --- Push path 1: local snapshot (SyncService.cs ~:74) -------------------------------------

    [RetryFact]
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

    [RetryFact]
    public async Task Sync_LocalPush_DropsMachineLocalTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var workspace = conn.CreateCommand();
            workspace.CommandText = """
                                    INSERT INTO workspaces (id, project_id, status, created_at)
                                    VALUES ('ws-1', 'acme', 'Active', 1)
                                    """;
            await workspace.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var queue = conn.CreateCommand();
            queue.CommandText = """
                                INSERT INTO promotion_queue (project_id, hash, path, value, source_file, score, reasons, scorer_version, created_at, updated_at)
                                VALUES ('acme', 'queue-hash', 'doc.md', 'queued value', 'doc.md', 1.0, '[]', 0, 1, 1)
                                """;
            await queue.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-local-machine-tables.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        var tables = await TableNamesAsync(pulledPath, TestContext.Current.CancellationToken);
        AssertNoCodeTables(tables);
        AssertNoMachineLocalTables(tables);
    }

    /// <summary>
    ///     Integration review S9: every other push test seeds embed_state='pending', so vec_code
    ///     stays empty and the strip's DROP TABLE IF EXISTS vec_code never actually tears down a
    ///     populated vec0 shadow-table set. Seeds a real embedded row (vec_code_au fires) and
    ///     proves the push both survives it and leaves nothing behind.
    /// </summary>
    [RetryFact]
    public async Task Sync_LocalPush_WithPopulatedVecCode_DropsSucceed_AndNoVecCodeTablesRemain()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedEmbeddedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-populated-vec-code.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoCodeTables(await TableNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    // --- Push path 2: merged snapshot (SyncService.cs ~:105) -----------------------------------

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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

    // --- Schema digest: a pushed snapshot must not lie about the shape it strips away -----------

    /// <summary>
    ///     Integration review S11: the strip drops code_entries/code_fts/vec_code but leaves
    ///     application_id stamped at SchemaDigest — the digest computed from a Ddl block that
    ///     still declares those tables. A snapshot restored as a working bank (e.g. by a human
    ///     recovering from a pushed backup) is opened through the ordinary MemorySchema.EnsureAsync
    ///     path, which reads a matching digest as "nothing to do" and skips the Ddl block entirely
    ///     — so the restored bank never gets code_entries back, permanently. The strip must reset
    ///     application_id so the next EnsureAsync re-runs the Ddl block and recreates the code
    ///     corpus tables it stripped.
    /// </summary>
    [RetryFact]
    public async Task Sync_LocalPush_ResetsSchemaDigest_SoARestoredSnapshotRecreatesCodeTables()
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
        var restoredPath = Path.Combine(_dataRoot, "restored-from-snapshot.db");
        await File.WriteAllBytesAsync(restoredPath, pulled.Data, TestContext.Current.CancellationToken);

        // A hand-restore opens the pushed snapshot exactly like any other bank, through the
        // ordinary EnsureAsync path.
        await using var restored = new SqliteConnection($"Data Source={restoredPath}");
        await restored.OpenAsync(TestContext.Current.CancellationToken);
        restored.EnableExtensions();
        restored.LoadVector();
        await MemorySchema.EnsureAsync(restored, TestContext.Current.CancellationToken);

        (await TableExistsAsync(restored, "code_entries")).ShouldBeTrue(
            "the strip must reset application_id, or a restored snapshot's EnsureAsync sees a " +
            "matching digest, skips the Ddl block, and never gets code_entries back.");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken) is not null;
    }

    // --- Backward compat: a pre-code-corpus snapshot has none of these tables ------------------

    /// <summary>
    ///     H6: snapshots are opened via <c>openSnapshot</c>, which never runs
    ///     <c>MemorySchema.EnsureAsync</c> — so the strip must tolerate a snapshot that predates
    ///     the code corpus (a bare <c>DROP TABLE</c> without <c>IF EXISTS</c> would abort the
    ///     push). Uses the legacy hand-rolled schema (no code tables at all), matching the shape
    ///     of a bank from before this feature.
    /// </summary>
    [RetryFact]
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

    // --- Encrypted bank: the code corpus must strip the same way through the password path -----

    private const string EncryptedKey = "sync-code-exclusion-encrypted-key";

    /// <summary>
    ///     Integration review S9: every other test in this file uses an unencrypted bank, so the
    ///     strip's encrypted path (SqliteConnectionStringBuilder.Password, mirrored from
    ///     SyncServiceEncryptedTests) never ran against a populated code corpus. Full production
    ///     schema via MemorySchema.EnsureAsync (as CreateAndOpenAsync does), just with a password
    ///     on every connection string.
    /// </summary>
    private static async Task<SqliteConnection> CreateAndOpenEncryptedAsync(string path, CancellationToken ct)
    {
        var conn = new SqliteConnection($"Data Source={path};Password={EncryptedKey}");
        await conn.OpenAsync(ct);
        conn.EnableExtensions();
        conn.LoadVector();
        await MemorySchema.EnsureAsync(conn, ct);
        return conn;
    }

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshotEncrypted() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Password={EncryptedKey}");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenReadOnlyEncrypted() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Password={EncryptedKey};Mode=ReadOnly");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    [RetryFact]
    public async Task Sync_LocalPush_EncryptedBank_WithPopulatedCodeCorpus_DropsCodeTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenEncryptedAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedEmbeddedCodeRowAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshotEncrypted(),
            OpenReadOnlyEncrypted(), TimeProvider.System, NullLogger<SyncService>.Instance);
        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);
        result.Sent.ShouldBe(1);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();

        // The pushed bytes carry an embedded authenticity header (S2) ahead of the encrypted
        // snapshot itself — strip it before opening, same as the pull side does.
        new SyncBlobAuthenticator().TryUnwrap(pulled.Data, out _, out var innerBytes).ShouldBeTrue(
            "a push against an encrypted bank must publish a wrapped blob carrying the embedded authenticity header");

        var pulledPath = Path.Combine(_dataRoot, "pulled-encrypted.db");
        await File.WriteAllBytesAsync(pulledPath, innerBytes, TestContext.Current.CancellationToken);

        // The pulled snapshot is itself encrypted (VACUUM INTO of an encrypted bank stays
        // encrypted, docs/work/archive/2026-08-06-sqlite3mc-feature-surface.md F9), so table
        // names must be read back through the same password.
        var tables = new List<string>();
        await using (var check = new SqliteConnection($"Data Source={pulledPath};Password={EncryptedKey};Mode=ReadOnly"))
        {
            await check.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        AssertNoCodeTables(tables);
    }

    // --- P2: telemetry (search_quality/metrics) never syncs (ADR-0095) -------------------------

    /// <summary>Seeds populated telemetry rows so the strip's DROP must tear down real content,
    /// not just empty tables — row counts cannot distinguish DROP from DELETE, so the oracle
    /// below asserts table/index absence from sqlite_master (mutation: DROP→DELETE fails).</summary>
    private static async Task SeedTelemetryAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var quality = conn.CreateCommand();
        quality.CommandText = """
                                INSERT INTO search_quality (correlation_id, query, scope, project_id, result_count, top_source_files, created_at)
                                VALUES ('corr-1', 'how to test sync strip', 'project', 'acme', 2, '["a.md","b.md"]', 1),
                                       ('corr-2', 'code query SELECT *', 'project', 'acme', 5, '[]', 2)
                                """;
        await quality.ExecuteNonQueryAsync(ct);

        await using var metrics = conn.CreateCommand();
        metrics.CommandText = """
                                INSERT INTO metrics (name, kind, value, unit, project_id, query_hash, correlation_id, recorded_at)
                                VALUES ('search.total', 'Histogram', 12.5, 'ms', 'acme', 'hash1', 'corr-1', 1),
                                       ('search.fts', 'Histogram', 3.2, 'ms', 'acme', NULL, 'corr-1', 2),
                                       ('search.vector', 'Histogram', 5.1, 'ms', 'acme', 'hash2', 'corr-2', 3)
                                """;
        await metrics.ExecuteNonQueryAsync(ct);

        await using var qualityCount = conn.CreateCommand();
        qualityCount.CommandText = "SELECT COUNT(*) FROM search_quality";
        var qCount = (long)(await qualityCount.ExecuteScalarAsync(ct))!;
        qCount.ShouldBe(2L, "search_quality must hold populated rows before the push runs, " +
                             "or this test proves nothing about a populated telemetry table.");

        await using var metricsCount = conn.CreateCommand();
        metricsCount.CommandText = "SELECT COUNT(*) FROM metrics";
        var mCount = (long)(await metricsCount.ExecuteScalarAsync(ct))!;
        mCount.ShouldBe(3L, "metrics must hold populated rows before the push runs, " +
                             "or this test proves nothing about a populated telemetry table.");
    }

    private static async Task<List<string>> MasterNamesAsync(string dbPath, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void AssertNoTelemetryTables(List<string> names)
    {
        names.ShouldNotContain("search_quality", "telemetry must never leave the machine via sync (ADR-0095).");
        names.ShouldNotContain("metrics");
        names.ShouldNotContain("idx_sq_project_time", "DROP must shed the telemetry indexes too — DELETE would leave them.");
        names.ShouldNotContain("idx_metrics_name_time");
        names.ShouldNotContain("idx_metrics_project_time");
        names.ShouldNotContain("idx_metrics_recorded_at");
        names.Any(n => n.Contains("search_quality", StringComparison.Ordinal) || n.Contains("metrics", StringComparison.Ordinal)).ShouldBeFalse(
            "no telemetry table, index, or autoindex remnant may survive the strip.");
    }

    private static async Task<List<string>> ReadSearchQualityRowsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var rows = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT correlation_id, query, scope, project_id, result_count, top_source_files, " +
                          "follow_through_count, follow_through_files, usefulness_grade, grade_note, created_at " +
                          "FROM search_quality ORDER BY correlation_id";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var parts = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, ct))
                {
                    parts.Add("<null>");
                }
                else
                {
                    parts.Add(reader.GetValue(i).ToString() ?? "<null>");
                }
            }

            rows.Add(string.Join("|", parts));
        }

        return rows;
    }

    private static async Task<List<string>> ReadMetricsRowsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var rows = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, kind, value, unit, project_id, query_hash, correlation_id, tags, recorded_at " +
                          "FROM metrics ORDER BY name, recorded_at, correlation_id";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var parts = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (await reader.IsDBNullAsync(i, ct))
                {
                    parts.Add("<null>");
                }
                else
                {
                    parts.Add(reader.GetValue(i).ToString() ?? "<null>");
                }
            }

            rows.Add(string.Join("|", parts));
        }

        return rows;
    }

    [RetryFact]
    public async Task Sync_LocalPush_DropsTelemetryTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedTelemetryAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-local-telemetry.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoTelemetryTables(await MasterNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    [RetryFact]
    public async Task Sync_MergedPush_DropsTelemetryTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed-telemetry.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedTelemetryAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);
        result.Received.ShouldBe(1, "the merge branch must actually run for this test to exercise the merged-push strip.");

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-merged-telemetry.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoTelemetryTables(await MasterNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    [RetryFact]
    public async Task Sync_RetryMergedPush_DropsTelemetryTablesFromSnapshot()
    {
        var inner = new FakeCloudStore();
        var cloud = new ConflictOnceCloudStore(inner);

        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed-retry-telemetry.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        inner.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedTelemetryAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        cloud.ConflictWasRaised.ShouldBeTrue("the test must actually exercise the conflict-retry (third) push path.");

        var pulled = await inner.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-retry-telemetry.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);

        AssertNoTelemetryTables(await MasterNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    [RetryFact]
    public async Task Sync_Pull_LeavesLocalTelemetryUntouched()
    {
        var cloud = new FakeCloudStore();
        var remoteSeedPath = Path.Combine(_dataRoot, "remote-seed-pull-telemetry.db");
        await SeedRemoteEntryAsync(remoteSeedPath, TestContext.Current.CancellationToken);
        cloud.Set("test-object", await File.ReadAllBytesAsync(remoteSeedPath, TestContext.Current.CancellationToken));

        List<string> beforeQuality;
        List<string> beforeMetrics;
        await using (var conn = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedTelemetryAsync(conn, TestContext.Current.CancellationToken);
            beforeQuality = await ReadSearchQualityRowsAsync(conn, TestContext.Current.CancellationToken);
            beforeMetrics = await ReadMetricsRowsAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenAsync(BankPath, ct), OpenSnapshot(), OpenReadOnly(),
            TimeProvider.System, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);

        List<string> afterQuality;
        List<string> afterMetrics;
        await using (var check = await CreateAndOpenAsync(BankPath, TestContext.Current.CancellationToken))
        {
            afterQuality = await ReadSearchQualityRowsAsync(check, TestContext.Current.CancellationToken);
            afterMetrics = await ReadMetricsRowsAsync(check, TestContext.Current.CancellationToken);
        }

        afterQuality.ShouldBe(beforeQuality, "a pull/merge must never touch local telemetry — content equality, not just counts.");
        afterMetrics.ShouldBe(beforeMetrics, "a pull/merge must never touch local telemetry — content equality, not just counts.");

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();
        var pulledPath = Path.Combine(_dataRoot, "pulled-pull-telemetry.db");
        await File.WriteAllBytesAsync(pulledPath, pulled.Data, TestContext.Current.CancellationToken);
        AssertNoTelemetryTables(await MasterNamesAsync(pulledPath, TestContext.Current.CancellationToken));
    }

    [RetryFact]
    public async Task Sync_PreTelemetrySnapshot_StripsWithoutError()
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
            "a pre-telemetry bank must still sync successfully — DROP TABLE IF EXISTS must not throw when the telemetry tables never existed.");
    }

    [RetryFact]
    public async Task Sync_LocalPush_EncryptedBank_WithPopulatedTelemetry_DropsTelemetryTablesFromSnapshot()
    {
        var cloud = new FakeCloudStore();
        await using (var conn = await CreateAndOpenEncryptedAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await SeedTelemetryAsync(conn, TestContext.Current.CancellationToken);
        }

        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshotEncrypted(),
            OpenReadOnlyEncrypted(), TimeProvider.System, NullLogger<SyncService>.Instance);
        var result = await service.MemorySyncAsync("acme", "test-object", TestContext.Current.CancellationToken);
        result.Sent.ShouldBe(1);

        var pulled = await cloud.PullAsync("test-object", TestContext.Current.CancellationToken);
        pulled.ShouldNotBeNull();

        new SyncBlobAuthenticator().TryUnwrap(pulled.Data, out _, out var innerBytes).ShouldBeTrue(
            "a push against an encrypted bank must publish a wrapped blob carrying the embedded authenticity header");

        var pulledPath = Path.Combine(_dataRoot, "pulled-encrypted-telemetry.db");
        await File.WriteAllBytesAsync(pulledPath, innerBytes, TestContext.Current.CancellationToken);

        var names = new List<string>();
        await using (var check = new SqliteConnection($"Data Source={pulledPath};Password={EncryptedKey};Mode=ReadOnly"))
        {
            await check.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master";
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }

        AssertNoTelemetryTables(names);
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
