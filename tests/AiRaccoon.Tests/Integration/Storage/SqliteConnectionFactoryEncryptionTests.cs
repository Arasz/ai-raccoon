using System.Data;
using System.Text;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryEncryptionTests : IDisposable
{
    // docs/plans/encryption-bitwarden-implementation.md §5.1 pinned vector: seed 00 01 … 1e 1f → x'72d2…'
    private const string DerivedHex = "72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485";
    private const string DerivedRawKey = $"x'{DerivedHex}'";

    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private static IEncryptionKeyResolver Resolver(InfrastructureOptions options, IEncryptionKeyProvider provider) =>
        new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)), [provider]);

    private SqliteConnectionFactory Factory(string? passphrase = null) => new(Options(), Resolver(Options(), new StubEncryptionKeyProvider(passphrase)));

    [RetryFact]
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

        var header = await File.ReadAllBytesAsync(factory.BankPath, TestContext.Current.CancellationToken);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldNotStartWith("SQLite format 3");

        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    /// <summary>
    ///     WP1-T08: the code corpus carries no encryption logic of its own — it rides the same
    ///     whole-bank encryption every other table gets, proven the same way
    ///     <see cref="OpenBankAsync_WithPassphrase_CreatesEncryptedDatabase" /> proves it for
    ///     <c>entries</c>: raw file bytes never contain the plaintext value.
    /// </summary>
    [RetryFact]
    public async Task OpenBankAsync_WithPassphrase_EncryptsCodeEntriesRows()
    {
        const string distinctiveValue = "quokka-narwhal-pangolin-secret-code-snippet";
        var factory = Factory("test-encryption-key");

        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                               INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                               VALUES ('h1', 'p1', @value, 'p1', 1, 1, 'acme', 1, 1)
                               """;
            cmd.Parameters.AddWithValue("@value", distinctiveValue);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // An encrypted bank must never leak code_entries content in its raw file bytes.
        var bytes = await File.ReadAllBytesAsync(factory.BankPath, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(bytes).ShouldNotContain(distinctiveValue);

        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using var check = reopen.CreateCommand();
        check.CommandText = "SELECT value FROM code_entries WHERE hash = 'h1'";
        (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(distinctiveValue);
    }

    [RetryFact]
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

        var header = await File.ReadAllBytesAsync(factory.BankPath, TestContext.Current.CancellationToken);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldStartWith("SQLite format 3");

        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    [RetryFact]
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

        var options = Options();
        var wrongFactory = new SqliteConnectionFactory(options, Resolver(options, new StubEncryptionKeyProvider("wrong-passphrase")));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await wrongFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    [RetryFact]
    public async Task RekeyBankAsync_PassphraseBankInWalMode_RekeysToDerivedRawKey()
    {
        var factory = Factory("env-passphrase");
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // WAL pins every factory open, but SQLCipher rekey is unsupported in WAL, so rekey switches
        // to DELETE journal mode internally; Pooling=false lets the probe close for real (a pooled connection would hold the lock).
        await using (var probe = new SqliteConnection($"Data Source={factory.BankPath};Password=env-passphrase;Pooling=false"))
        {
            await probe.OpenAsync(TestContext.Current.CancellationToken);
            await using var jm = probe.CreateCommand();
            jm.CommandText = "PRAGMA journal_mode";
            (await jm.ExecuteScalarAsync(TestContext.Current.CancellationToken))!.ToString().ShouldBe("wal");
        }

        await factory.RekeyBankAsync(DerivedRawKey, TestContext.Current.CancellationToken);

        // The bank now opens with the derived raw key…
        var derivedOptions = Options();
        var derivedFactory = new SqliteConnectionFactory(derivedOptions,
            Resolver(derivedOptions, new StubEncryptionKeyProvider(DerivedRawKey)));
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

    [RetryFact]
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

        var derivedOptions = Options();
        var derivedFactory = new SqliteConnectionFactory(derivedOptions,
            Resolver(derivedOptions, new StubEncryptionKeyProvider(DerivedRawKey)));
        await using var reopen = await derivedFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);

        // The bank is encrypted now, not plaintext.
        var header = await File.ReadAllBytesAsync(factory.BankPath, TestContext.Current.CancellationToken);
        Encoding.ASCII.GetString(header[..16]).ShouldNotStartWith("SQLite format 3");
    }

    [RetryFact]
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

    /// <summary>
    ///     Neither the bitwarden source's current nor its legacy derivation opens a bank keyed to
    ///     something else, so the open is refused. The underlying SQLite code stays reachable.
    /// </summary>
    [RetryFact]
    public async Task OpenBankAsync_ResolverReturnsDifferentKey_ThrowsKeyMismatchOverSqlite26()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var bankFactory = new SqliteConnectionFactory(options, Resolver(options, new StubEncryptionKeyProvider("bank-key-A")));
        await using (var connection = await bankFactory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Sidecar points at bitwarden; the fake bws secret derives a key that is NOT bank-key-A.
        await File.WriteAllTextAsync($"{bankFactory.BankPath}.source", """{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""", TestContext.Current.CancellationToken);
        var resolver = new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
            [new StubEncryptionKeyProvider(null), new BitwardenEncryptionKeyProvider(new FakeBwsRunner(new BwsResult(0, ValidEd25519Pem(), "")))]);
        var resolverFactory = new SqliteConnectionFactory(options, resolver);

        var ex = await Should.ThrowAsync<BankKeyMismatchException>(async () =>
        {
            await using var conn = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.InnerException.ShouldBeOfType<SqliteException>().SqliteErrorCode.ShouldBe(26);
    }

    [RetryFact]
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

        // No cached/offline key copy (docs/plans/encryption-bitwarden-implementation.md D4): the
        // directory holds only the bank and SQLite's own journal artifacts, none containing the derived key material.
        var files = Directory.GetFiles(_dataRoot);
        files.ShouldAllBe(file =>
            file == factory.BankPath
            || file.EndsWith("-wal", StringComparison.Ordinal)
            || file.EndsWith("-shm", StringComparison.Ordinal));
        foreach (var file in files)
        {
            (await File.ReadAllTextAsync(file, Encoding.Latin1, TestContext.Current.CancellationToken)).ShouldNotContain(DerivedHex);
        }
    }

    /// <summary>
    ///     The migrate verb's current-key probe must report "nothing to do" without touching the
    ///     bank (docs/plans/2026-08-07-hkdf-rekey-migration.md item 3). A bank with no schema
    ///     proves InitializeAsync's MemorySchema.EnsureAsync never ran on the success path.
    /// </summary>
    [RetryFact]
    public async Task MigrateLegacyKeyAsync_BankAlreadyOnCurrentKey_CreatesNoSchemaObjects()
    {
        const string passphrase = "current-key";
        var factory = Factory(passphrase);
        Directory.CreateDirectory(Path.GetDirectoryName(factory.BankPath)!);

        // Keyed correctly but never opened through the factory, so it carries no application schema.
        await using (var raw = new SqliteConnection($"Data Source={factory.BankPath};Password={passphrase};Mode=ReadWriteCreate"))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
        }

        var rekeyed = await factory.MigrateLegacyKeyAsync(TestContext.Current.CancellationToken);

        rekeyed.ShouldBeFalse();
        await using var check = new SqliteConnection($"Data Source={factory.BankPath};Password={passphrase};Pooling=false");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = check.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master";
        var tableCount = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        tableCount.ShouldBe(0L, "the current-key probe must not run MemorySchema.EnsureAsync — it only proves the key is already current");
    }

    /// <summary>
    ///     RekeyBankAsync's explicit-current-key overload must refuse before attempting any rekey —
    ///     see docs/plans/2026-08-07-hkdf-rekey-migration.md test 10.
    /// </summary>
    [RetryFact]
    public async Task RekeyBankAsync_WrongCurrentKey_ThrowsBeforeAnyRekeyIsAttempted()
    {
        var factory = Factory("actual-current-key");
        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
            await factory.RekeyBankAsync(DerivedRawKey, "wrong-current-key", TestContext.Current.CancellationToken));
        ex.SqliteErrorCode.ShouldBe(26);

        // Nothing was rekeyed: the real current key still opens the bank...
        await using (var stillCurrent = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            stillCurrent.State.ShouldBe(ConnectionState.Open);
        }

        // ...and the key that would have been the rekey target does not.
        var newKeyFactory = new SqliteConnectionFactory(Options(), Resolver(Options(), new StubEncryptionKeyProvider(DerivedRawKey)));
        await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await newKeyFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
    }

    /// <summary>.NET-F2: a Bitwarden-sourced key is cached across bank opens — N opens shell out to bws once, not N times.</summary>
    [RetryFact]
    public async Task OpenBankAsync_CalledManyTimesWithBitwardenSource_InvokesBwsExactlyOnce()
    {
        var options = Options();
        await File.WriteAllTextAsync($"{SqliteConnectionFactory.BankPathFor(options)}.source",
            """{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""", TestContext.Current.CancellationToken);
        var runner = new FakeBwsRunner(new BwsResult(0, ValidEd25519Pem(), ""));
        var resolver = new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
            [new StubEncryptionKeyProvider(null), new BitwardenEncryptionKeyProvider(runner)]);
        var factory = new SqliteConnectionFactory(options, resolver);

        for (var i = 0; i < 5; i++)
        {
            await using var conn = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        }

        runner.CallCount.ShouldBe(1);
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    /// <summary>Valid unencrypted ed25519 openssh-key-v1 PEM built from synthetic bytes (seed 00..1f).</summary>
    private static string ValidEd25519Pem()
    {
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var pub = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        using var body = new MemoryStream();
        body.Write("openssh-key-v1\0"u8);
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

        return $"-----BEGIN OPENSSH PRIVATE KEY-----\n{Convert.ToBase64String(body.ToArray())}\n-----END OPENSSH PRIVATE KEY-----\n";
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
        public int CallCount { get; private set; }

        public Task<BwsResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubEncryptionKeyProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Passphrase(Source) { Value = passphrase });
    }
}
