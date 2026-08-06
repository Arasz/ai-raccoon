using System.Data;
using System.Text;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryEncryptionTests : IDisposable
{
    // §5.1 pinned vector: seed 00 01 … 1e 1f → x'277b…'
    private const string DerivedHex = "277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b";
    private const string DerivedRawKey = "x'" + DerivedHex + "'";

    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private SqliteConnectionFactory Factory(string? passphrase = null) =>
        new(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider(passphrase));

    [Fact]
    public async Task OpenBankAsync_WithPassphrase_CreatesEncryptedDatabase()
    {
        var passphrase = "test-encryption-key";
        var factory = Factory(passphrase);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(ConnectionState.Open);
        File.Exists(factory.BankPath).ShouldBeTrue();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync();

        // Verify encryption: file header is not the standard SQLite header
        var header = File.ReadAllBytes(factory.BankPath);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldNotStartWith("SQLite format 3");

        // Verify the DB can be reopened with the correct passphrase
        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenBankAsync_WithoutPassphrase_OpensUnencryptedDatabase()
    {
        var factory = Factory(passphrase: null);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(ConnectionState.Open);
        File.Exists(factory.BankPath).ShouldBeTrue();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync();

        // Verify unencrypted: standard header
        var header = File.ReadAllBytes(factory.BankPath);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldStartWith("SQLite format 3");

        // Reopening without passphrase must succeed
        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenBankAsync_WithWrongPassphrase_FailsToOpen()
    {
        var passphrase = "correct-passphrase";
        var factory = Factory(passphrase);

        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var wrongFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider("wrong-passphrase"));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await wrongFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task RekeyBankAsync_PassphraseBankInWalMode_RekeysToDerivedRawKey()
    {
        var factory = Factory("env-passphrase");
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // The bank is in WAL mode (every factory open pins it) — rekey must switch to DELETE
        // journal mode internally (SQLCipher rekey is unsupported in WAL; plan §3.3).
        // Pooling=false so the probe closes for real (a pooled connection would hold the lock).
        await using (var probe = new SqliteConnection($"Data Source={factory.BankPath};Password=env-passphrase;Pooling=false"))
        {
            await probe.OpenAsync(TestContext.Current.CancellationToken);
            await using var jm = probe.CreateCommand();
            jm.CommandText = "PRAGMA journal_mode";
            (await jm.ExecuteScalarAsync(TestContext.Current.CancellationToken))!.ToString().ShouldBe("wal");
        }

        await factory.RekeyBankAsync(DerivedRawKey, TestContext.Current.CancellationToken);

        // The bank now opens with the derived raw key…
        var derivedFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider(DerivedRawKey));
        await using (var reopen = await derivedFactory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var check = reopen.CreateCommand();
            check.CommandText = "SELECT count(*) FROM t";
            (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
        }

        // …and the old passphrase no longer works.
        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var old = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task RekeyBankAsync_PlaintextBank_RekeysToDerivedRawKey()
    {
        var factory = Factory(passphrase: null);
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await factory.RekeyBankAsync(DerivedRawKey, TestContext.Current.CancellationToken);

        var derivedFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider(DerivedRawKey));
        await using var reopen = await derivedFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);

        // The bank is encrypted now, not plaintext.
        var header = File.ReadAllBytes(factory.BankPath);
        Encoding.ASCII.GetString(header[..16]).ShouldNotStartWith("SQLite format 3");
    }

    [Fact]
    public async Task OpenBankWithKeyAsync_WrongKey_ThrowsSqliteException26()
    {
        var factory = Factory("correct-key");
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await factory.OpenBankWithKeyAsync("wrong-key", TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task OpenBankAsync_ResolverReturnsDifferentKey_ThrowsSqliteException26()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var bankFactory = new SqliteConnectionFactory(options, new StubEncryptionKeyProvider("bank-key-A"));
        await using (var connection = await bankFactory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Sidecar points at bitwarden; the fake bws secret derives a key that is NOT bank-key-A.
        File.WriteAllText(bankFactory.BankPath + ".source", """{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var resolver = new EncryptionKeyResolver(new StubEncryptionKeyProvider(null), new FakeBwsRunner(new BwsResult(0, ValidEd25519Pem(), "")));
        var resolverFactory = new SqliteConnectionFactory(options, resolver);

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task RekeyBankAsync_DoesNotWriteKeyMaterialNextToBank()
    {
        var factory = Factory("env-passphrase");
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await factory.RekeyBankAsync(DerivedRawKey, TestContext.Current.CancellationToken);

        // D4: no cached/offline key copy — the directory holds only the bank and SQLite's own
        // journal artifacts, and none of them contains the derived key material.
        var files = Directory.GetFiles(_dataRoot);
        files.ShouldAllBe(file =>
            file == factory.BankPath
            || file.EndsWith("-wal", StringComparison.Ordinal)
            || file.EndsWith("-shm", StringComparison.Ordinal));
        foreach (var file in files)
        {
            File.ReadAllText(file, Encoding.Latin1).ShouldNotContain(DerivedHex);
        }
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    /// <summary>Valid unencrypted ed25519 openssh-key-v1 PEM built from synthetic bytes (seed 00..1f).</summary>
    private static string ValidEd25519Pem()
    {
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pub = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        WriteString(body, "none");
        WriteString(body, "none");
        WriteString(body, []);
        WriteUInt32(body, 1);
        using (var pubBlob = new MemoryStream())
        {
            WriteString(pubBlob, "ssh-ed25519");
            WriteString(pubBlob, pub);
            WriteString(body, pubBlob.ToArray());
        }

        using var priv = new MemoryStream();
        WriteUInt32(priv, 0x01234567);
        WriteUInt32(priv, 0x01234567);
        WriteString(priv, "ssh-ed25519");
        WriteString(priv, pub);
        WriteString(priv, [.. seed, .. pub]);
        WriteString(priv, []);
        priv.Write(new byte[8 - (int)priv.Length % 8]);
        WriteString(body, priv.ToArray());

        return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(body.ToArray())
                                                       + "\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static void WriteUInt32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private sealed class FakeBwsRunner(BwsResult result) : ICliSecretManager
    {
        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout) => result;
    }

    private sealed class StubEncryptionKeyProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string? GetPassphrase() => passphrase;
    }
}
