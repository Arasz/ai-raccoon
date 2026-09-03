using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     A remote sync blob is only ever integrity-checked (PRAGMA quick_check — detects
///     corruption, not substitution) before being ATTACHed into the live bank. These tests pin an
///     HMAC authenticity tag embedded directly in the pushed bytes (not a separate sidecar object
///     — a torn write between two objects, or an attacker with delete-only access stripping the
///     sidecar, would otherwise defeat the check) that is verified before both quick_check and
///     ATTACH: a valid-but-substituted blob must be refused, an untampered round trip must be
///     unaffected, a legacy remote blob with no tag follows the documented accept-with-warning
///     migration path, and a downgrade attempt against an objectKey that has already carried a
///     verified tag is refused rather than silently trusted (S2,
///     docs/work/2026-08-21-delta-review-fix-plan.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceRemoteBlobTests : IDisposable
{
    private const string Key = "remote-blob-test-key";
    private readonly string _dataRoot = TestData.CreateTempRoot("sync-remote-blob-test");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static string EntriesSchema => """
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
                                            CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NULL, created_at INTEGER NOT NULL);
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

    private static async Task<SqliteConnection> CreateAndOpenEncryptedAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={path};Password={Key}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = EntriesSchema;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static async Task<SqliteConnection> CreateAndOpenUnencryptedAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = EntriesSchema;
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

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenSnapshotUnencrypted() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path}");
            await conn.OpenAsync(ct);
            conn.EnableExtensions();
            conn.LoadVector();
            return conn;
        };

    private static Func<string, CancellationToken, Task<SqliteConnection>> OpenReadOnlyUnencrypted() =>
        async (path, ct) =>
        {
            var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
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

    private static async Task InsertEntryUnencryptedAsync(string path, string hash, string entryPath, string value,
        CancellationToken ct)
    {
        await using var conn = await CreateAndOpenUnencryptedAsync(path, ct);
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
    /// authenticity, not integrity, is what catches it. Also pins the ORDER: the check must run
    /// before quick_check is ever attempted on the remote — a mismatched count would mean
    /// quick_check (and therefore ATTACH, right after it) was reached first.</summary>
    [RetryFact]
    public async Task ATamperedRemoteBlob_IsRefusedBeforeQuickCheckOrAttach()
    {
        var cloud = new FakeCloudStore();
        var openReadOnlyCallCount = 0;
        Func<string, CancellationToken, Task<SqliteConnection>> countingOpenReadOnly = async (path, ct) =>
        {
            openReadOnlyCallCount++;
            return await OpenReadOnly()(path, ct);
        };

        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshot(),
            countingOpenReadOnly, TimeProvider.System, NullLoggerFor());

        await InsertEntryAsync(BankPath, "h1", "p1.md", "original", TestContext.Current.CancellationToken);
        await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        // Substitute the uploaded blob's content: unwrap the pushed bytes (production
        // authenticator, not a re-implementation), edit a row on the inner SQLite bytes via a
        // separate connection (still structurally valid — quick_check reports "ok"), then
        // re-wrap with the ORIGINAL tag — exactly what an attacker who can write but not derive
        // the key would produce.
        var pushed = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        pushed.ShouldNotBeNull();
        var authenticator = new SyncBlobAuthenticator();
        authenticator.TryUnwrap(pushed.Data, out var originalTag, out var innerBytes).ShouldBeTrue(
            "a push against an encrypted bank must publish a wrapped blob carrying the embedded authenticity header");

        var tamperedPath = Path.Combine(_dataRoot, "tampered.db");
        await File.WriteAllBytesAsync(tamperedPath, innerBytes, TestContext.Current.CancellationToken);
        await using (var tamperConn = new SqliteConnection($"Data Source={tamperedPath};Password={Key}"))
        {
            await tamperConn.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = tamperConn.CreateCommand();
            update.CommandText = "UPDATE entries SET value = 'TAMPERED', hash = 'tampered-hash' WHERE hash = 'h1'";
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var tamperedInnerBytes = await File.ReadAllBytesAsync(tamperedPath, TestContext.Current.CancellationToken);

        // Prove the substituted inner file is itself structurally intact — this is not the
        // corrupt-file case.
        await using (var verify = new SqliteConnection($"Data Source={tamperedPath};Password={Key};Mode=ReadOnly"))
        {
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = (string)(await check.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            result.ShouldBe("ok", "the substituted file must remain structurally valid — this test proves authenticity catches it, not integrity");
        }

        // Reconstruct header + ORIGINAL tag + tampered content — exactly what an attacker who can
        // write the object but cannot derive the key would produce. The header bytes are copied
        // from the real pushed blob rather than hardcoded, so this test does not know or assume
        // the header's exact contents.
        var headerLength = pushed.Data.Length - originalTag.Length - innerBytes.Length;
        var rewrapped = new byte[headerLength + originalTag.Length + tamperedInnerBytes.Length];
        Array.Copy(pushed.Data, 0, rewrapped, 0, headerLength);
        Array.Copy(originalTag, 0, rewrapped, headerLength, originalTag.Length);
        Array.Copy(tamperedInnerBytes, 0, rewrapped, headerLength + originalTag.Length, tamperedInnerBytes.Length);
        cloud.Set("obj", rewrapped);

        openReadOnlyCallCount = 0; // only count calls made during the tampered sync attempt below

        await Should.ThrowAsync<SyncTamperedRemoteException>(() =>
            service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken));

        openReadOnlyCallCount.ShouldBe(1,
            "only the local snapshot's own quick_check may run before a tampered remote is refused — a " +
            "second call would mean quick_check (and therefore ATTACH, right after it) was reached for the " +
            "remote blob, proving the authenticity check ran too late");

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
    /// this change: the embedded tag matches, so the authenticity check is a no-op on the happy
    /// path.</summary>
    [RetryFact]
    public async Task AnUntamperedRoundTrip_VerifiesAndMergesAsToday()
    {
        var cloud = new FakeCloudStore();

        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);

        // A prior push from a peer bank, using the real push path, establishes an authenticated
        // remote — not a manually-seeded raw VACUUM INTO snapshot.
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

        var pushedRaw = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        pushedRaw.ShouldNotBeNull();
        new SyncBlobAuthenticator().TryUnwrap(pushedRaw.Data, out _, out _).ShouldBeTrue(
            "a push against an encrypted bank must publish a wrapped blob carrying the embedded authenticity header");
    }

    /// <summary>A remote blob pushed before this feature existed carries no embedded header. The
    /// documented migration behavior, the first time this objectKey is seen, is accept-with-warning.</summary>
    [RetryFact]
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

        // Simulate a pre-existing remote object pushed before this feature existed — headerless.
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
            "a missing tag on an otherwise-encrypted remote, seen for the first time, must be logged as a warning, not silently accepted");
    }

    /// <summary>Once this bank has pushed (or verified) a tagged blob for an objectKey, a later
    /// headerless blob for that SAME objectKey is refused, not warned-and-accepted — it can only
    /// arise from an attacker rewriting the whole object without the key (a delete-only attacker
    /// could otherwise strip a separate sidecar tag at zero cost; embedding removes that path, and
    /// this TOFU watermark closes the "just replace the object with a headerless one" variant).</summary>
    [RetryFact]
    public async Task ADowngradeToAHeaderlessBlob_AfterATagHasBeenSeenForThisObjectKey_IsRefused()
    {
        var cloud = new FakeCloudStore();
        var service = new SyncService(cloud, ct => CreateAndOpenEncryptedAsync(BankPath, ct), OpenSnapshot(),
            OpenReadOnly(), TimeProvider.System, NullLoggerFor());

        await InsertEntryAsync(BankPath, "h1", "p1.md", "original", TestContext.Current.CancellationToken);
        await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        // Attacker (or a hand-restored old backup) rewrites the whole object without a tag — same
        // content, tag stripped.
        var pushed = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        pushed.ShouldNotBeNull();
        new SyncBlobAuthenticator().TryUnwrap(pushed.Data, out _, out var innerBytes).ShouldBeTrue();
        cloud.Set("obj", innerBytes);

        await Should.ThrowAsync<SyncTamperedRemoteException>(() =>
            service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken));
    }

    /// <summary>Pins the push-side skip: an unencrypted bank has no passphrase to derive a key
    /// from and must publish raw, unwrapped bytes — if the guard were silently deleted, every push
    /// would wrap with an empty-string key and this would fail.</summary>
    [RetryFact]
    public async Task MemorySync_UnencryptedBank_PushesRawBytesWithoutAWrapHeader()
    {
        var cloud = new FakeCloudStore();
        var service = new SyncService(cloud, ct => CreateAndOpenUnencryptedAsync(BankPath, ct), OpenSnapshotUnencrypted(),
            OpenReadOnlyUnencrypted(), TimeProvider.System, NullLoggerFor());

        await InsertEntryUnencryptedAsync(BankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);
        await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        var pushed = await cloud.PullAsync("obj", TestContext.Current.CancellationToken);
        pushed.ShouldNotBeNull();
        new SyncBlobAuthenticator().TryUnwrap(pushed.Data, out _, out _).ShouldBeFalse(
            "an unencrypted bank has no passphrase to derive a key from and must push raw, unwrapped bytes");
    }

    /// <summary>Pins the pull-side skip: an unencrypted bank must not attempt verification at all
    /// (Debug-level skip log only) — if the guard were silently deleted, a headerless remote would
    /// instead take the legacy accept-with-warning branch (Warning-level "no authenticity tag").</summary>
    [RetryFact]
    public async Task MemorySync_UnencryptedBank_SkipsAuthenticityVerificationOnPull()
    {
        var cloud = new FakeCloudStore();

        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryUnencryptedAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);
        var remoteSnapshotPath = Path.Combine(_dataRoot, "remote-snapshot.db");
        await using (var conn = await CreateAndOpenUnencryptedAsync(remoteBankPath, TestContext.Current.CancellationToken))
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{remoteSnapshotPath}'";
            await vac.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        cloud.Set("obj", await File.ReadAllBytesAsync(remoteSnapshotPath, TestContext.Current.CancellationToken));

        await InsertEntryUnencryptedAsync(BankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var logger = new FakeLogger<SyncService>();
        var service = new SyncService(cloud, ct => CreateAndOpenUnencryptedAsync(BankPath, ct), OpenSnapshotUnencrypted(),
            OpenReadOnlyUnencrypted(), TimeProvider.System, logger);

        await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);

        var records = logger.Collector.GetSnapshot();
        records.ShouldContain(r => r.Level == LogLevel.Debug
                                    && r.Message.Contains("unencrypted", StringComparison.OrdinalIgnoreCase),
            "an unencrypted bank must log that verification was skipped outright");
        records.ShouldNotContain(r => r.Message.Contains("no authenticity tag", StringComparison.OrdinalIgnoreCase),
            "an unencrypted bank has nothing to verify against and must skip the check, not treat every remote as an unverified legacy blob");
    }

    private static ILogger<SyncService> NullLoggerFor() => new FakeLogger<SyncService>();
}
