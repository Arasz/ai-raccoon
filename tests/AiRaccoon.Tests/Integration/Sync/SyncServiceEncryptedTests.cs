using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

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
    private readonly SyncTestBank _bank;

    private readonly string _dataRoot;

    public SyncServiceEncryptedTests()
    {
        _dataRoot = TestData.CreateTempRoot("sync-encrypted");
        _bankPath = Path.Combine(_dataRoot, "memory.db");
        _bank = new SyncTestBank(_bankPath, Key);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task InsertEntryAsync(string path, string hash, string entryPath, string value,
        CancellationToken ct)
    {
        await using var conn = await _bank.CreateAndOpenAsync(path, ct);
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

    [RetryFact]
    public async Task MemorySync_EncryptedBank_PushesEncryptedSnapshot()
    {
        var cloud = new FakeCloudStore();
        var service = _bank.CreateService(cloud);

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

    [RetryFact]
    public async Task MemorySync_EncryptedBank_MergesEncryptedRemote()
    {
        var cloud = new FakeCloudStore();

        // Seed the remote with an encrypted snapshot from a second bank holding a different entry.
        var remoteBankPath = Path.Combine(_dataRoot, "remote.db");
        await InsertEntryAsync(remoteBankPath, "h2", "p2.md", "v2", TestContext.Current.CancellationToken);
        var remoteSnapshotPath = Path.Combine(_dataRoot, "remote-snapshot.db");
        await using (var conn = await _bank.CreateAndOpenAsync(remoteBankPath, TestContext.Current.CancellationToken))
        {
            await using var vac = conn.CreateCommand();
            vac.CommandText = $"VACUUM INTO '{remoteSnapshotPath}'";
            await vac.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        cloud.Set("obj", await File.ReadAllBytesAsync(remoteSnapshotPath, TestContext.Current.CancellationToken));

        await InsertEntryAsync(_bankPath, "h1", "p1.md", "v1", TestContext.Current.CancellationToken);

        var service = _bank.CreateService(cloud);

        var result = await service.MemorySyncAsync("acme", "obj", TestContext.Current.CancellationToken);
        result.Received.ShouldBe(1);

        await using (var conn = await _bank.CreateAndOpenAsync(_bankPath, TestContext.Current.CancellationToken))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM entries";
            (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2L);
        }
    }
}
