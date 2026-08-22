using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     A remote sync blob is only ever integrity-checked (PRAGMA quick_check — detects
///     corruption, not substitution) before being ATTACHed into the live bank. These tests pin
///     an HMAC authenticity check that runs before both quick_check and ATTACH: a valid-but-
///     substituted blob must be refused, an untampered round trip must be unaffected, and a
///     legacy remote blob with no tag must follow the documented accept-with-warning migration
///     path (S2, docs/work/2026-08-21-delta-review-fix-plan.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceRemoteBlobTests : IDisposable
{
    private const string Key = "remote-blob-test-key";
    private readonly string _dataRoot = TestData.CreateTempRoot("sync-remote-blob-test");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static async Task<SqliteConnection> CreateAndOpenEncryptedAsync(string path, CancellationToken ct = default)
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

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshot() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Password={Key}");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenReadOnly() =>
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
        await using var conn = await CreateAndOpenEncryptedAsync(path, ct);
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

    /// <summary>A valid-but-substituted remote blob (a row edited in place after upload, so
    /// PRAGMA quick_check still reports "ok") must be refused before ATTACH — proving
    /// authenticity, not integrity, is what catches it.</summary>
    [Fact]
    public async Task ATamperedRemoteBlob_IsRefusedBeforeAttach()
    {
        var cloud = new FakeCloudStore();
        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshot(),
            OpenReadOnly(), TimeProvider.System, NullLoggerFor());

        await InsertEntryAsync(BankPath, "h1", "p1.md", "original", TestContext.Current.CancellationToken);
        await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        // Substitute the uploaded blob's content via a separate connection, keeping it a
        // structurally valid, still-encrypted SQLite file (quick_check reports "ok") — the
        // sidecar HMAC tag pushed alongside the original blob is left untouched.
        var pushed = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        pushed.ShouldNotBeNull();
        var tamperedPath = Path.Combine(_dataRoot, "tampered.db");
        await File.WriteAllBytesAsync(tamperedPath, pushed.Data, TestContext.Current.CancellationToken);
        await using (var tamperConn = new SqliteConnection($"Data Source={tamperedPath};Password={Key}"))
        {
            await tamperConn.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = tamperConn.CreateCommand();
            update.CommandText = "UPDATE entries SET value = 'TAMPERED', hash = 'tampered-hash' WHERE hash = 'h1'";
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var tamperedBytes = await File.ReadAllBytesAsync(tamperedPath, TestContext.Current.CancellationToken);
        cloud.Set("obj", tamperedBytes); // only replaces "obj" — "obj.hmac" still carries the original tag

        // Prove the substituted file is itself structurally intact — this is not the corrupt-file case.
        await using (var verify = new SqliteConnection($"Data Source={tamperedPath};Password={Key};Mode=ReadOnly"))
        {
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = (string)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            result.ShouldBe("ok", "the substituted file must remain structurally valid — this test proves authenticity catches it, not integrity");
        }

        await Should.ThrowAsync<SyncTamperedRemoteException>(() =>
            service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken));

        await using (var conn = await CreateAndOpenEncryptedAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM entries WHERE hash = 'tampered-hash'";
            var countScalar = await check.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            countScalar.ShouldNotBeNull();
            ((long)countScalar).ShouldBe(0, "a refused tampered remote blob must never reach the live bank");

            await using var original = conn.CreateCommand();
            original.CommandText = "SELECT value FROM entries WHERE hash = 'h1'";
            var originalValue = (string?)await original.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            originalValue.ShouldBe("original", "the live bank must be untouched by a refused merge");
        }
    }

    /// <summary>An untampered push-then-pull round trip must verify and merge exactly as before
    /// this change: the HMAC tag matches, so the authenticity check is a no-op on the happy
    /// path.</summary>
    [Fact]
    public async Task AnUntamperedRoundTrip_VerifiesAndMergesAsToday()
    {
        var cloud = new FakeCloudStore();

        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);
        var remoteSnapshotPath = Path.Combine(_dataRoot, "remote-snapshot.db");
        await using (var conn = await CreateAndOpenEncryptedAsync(remoteBankPath, TestContext.Current.CancellationToken))
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{remoteSnapshotPath}'";
            await vac.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var remoteBytes = await File.ReadAllBytesAsync(remoteSnapshotPath, TestContext.Current.CancellationToken);
        cloud.Set("obj", remoteBytes);

        // Push a companion tag as a real prior sync would have, so the pull side sees a
        // properly-tagged remote instead of a legacy one.
        var remoteService = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(remoteBankPath, ct), OpenSnapshot(),
            OpenReadOnly(), TimeProvider.System, NullLoggerFor());
        await remoteService.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        await InsertEntryAsync(BankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshot(),
            OpenReadOnly(), TimeProvider.System, NullLoggerFor());

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Received.ShouldBeGreaterThanOrEqualTo(1);

        await using (var conn = await CreateAndOpenEncryptedAsync(BankPath, TestContext.Current.CancellationToken))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entries";
            var countScalar = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            countScalar.ShouldNotBeNull();
            ((long)countScalar).ShouldBeGreaterThanOrEqualTo(2, "both the local and merged remote rows must be present");
        }

        var tag = await cloud.PullAsync("obj.hmac", TestContext.Current.CancellationToken);
        tag.ShouldNotBeNull("a push against an encrypted bank must publish a companion authenticity tag");
    }

    /// <summary>A remote blob pushed before this feature existed carries no ".hmac" sidecar
    /// object. The documented migration behavior is accept-with-warning, not refusal.</summary>
    [Fact]
    public async Task ALegacyRemoteBlobWithoutATag_MergesWithALoggedWarning()
    {
        var cloud = new FakeCloudStore();

        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);
        var remoteSnapshotPath = Path.Combine(_dataRoot, "remote-snapshot.db");
        await using (var conn = await CreateAndOpenEncryptedAsync(remoteBankPath, TestContext.Current.CancellationToken))
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{remoteSnapshotPath}'";
            await vac.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Simulate a pre-existing remote object with no sidecar tag ever pushed — legacy blob.
        cloud.Set("obj", await File.ReadAllBytesAsync(remoteSnapshotPath, TestContext.Current.CancellationToken));

        await InsertEntryAsync(BankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var logger = new FakeLogger<SyncService>();
        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshot(),
            OpenReadOnly(), TimeProvider.System, logger);

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Received.ShouldBeGreaterThanOrEqualTo(1, "a legacy remote blob without a tag must still merge — accept-with-warning, not refusal");

        var records = logger.Collector.GetSnapshot();
        records.ShouldContain(r => r.Level == LogLevel.Warning
                                    && r.Message.Contains("no authenticity tag", StringComparison.OrdinalIgnoreCase),
            "a missing tag on an otherwise-encrypted remote must be logged as a warning, not silently accepted");
    }

    private static ILogger<SyncService> NullLoggerFor() => new FakeLogger<SyncService>();
}
