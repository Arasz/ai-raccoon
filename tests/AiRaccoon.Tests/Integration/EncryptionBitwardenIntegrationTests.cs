using System.Data;
using System.Diagnostics;
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

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     End-to-end through the real stack: a fake `bws` shell script at an absolute path (no PATH
///     mutation) → BitwardenCliSecretManager → BitwardenEncryptionKeyProvider → EncryptionKeyResolver →
///     SqliteConnectionFactory against real temp SQLCipher banks (plan §S4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(Unit.Encryption.BwsAccessTokenCollection.Name)]
public sealed class EncryptionBitwardenIntegrationTests : IDisposable
{
    // §5.1 pinned vector — seed 00 01 … 1e 1f derives to exactly this x'…' (hard-coded, never recomputed).
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    // The same seed under the pre-ADR-0012 construction SHA-256(Label ‖ seed) — what an installed
    // user's bank is encrypted with before the migration runs. Independently derived, never recomputed.
    private const string LegacyDerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    // Owner-default project/secret ids (plan D6). The fake bws serves the synthetic key for SecretId.
    private const string ProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string SecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";
    private const string WrongKeySecretId = "wrong-key-secret-id"; // fake serves key2.pem → a different derived key
    private const string GarbageSecretId = "garbage-secret-id"; // fake prints non-key stdout
    private const string SleepSecretId = "sleep-secret-id"; // fake sleeps (timeout leg)
    private const string UnknownSecretId = "unknown-secret-id"; // fake exits 1 "secret not found"

    // Obviously fake; the fake bws accepts exactly this token, from BWS_ACCESS_TOKEN or argv -t.
    private const string KnownToken = "test-bws-token-0123";

    // fake bws: emulates `bws secret get <id>` (plan §5.3). A token may arrive via argv `-t <token>`
    // or the inherited BWS_ACCESS_TOKEN; when one is present it must equal the known test token,
    // else bws-style stderr + exit 1. No token → serve the key (the plan's fixture recipe).
    private const string FakeBwsScript = """
                                         #!/bin/sh
                                         DIR="$(dirname "$0")"
                                         TOKEN=""
                                         ID=""
                                         while [ "$#" -gt 0 ]; do
                                           case "$1" in
                                             -t) TOKEN="$2"; shift 2 ;;
                                             get) ID="$2"; shift 2 ;;
                                             *) shift ;;
                                           esac
                                         done
                                         if [ -z "$ID" ]; then echo "bws: missing secret id" >&2; exit 1; fi
                                         if [ -z "$TOKEN" ]; then TOKEN="$BWS_ACCESS_TOKEN"; fi
                                         if [ -n "$TOKEN" ] && [ "$TOKEN" != "test-bws-token-0123" ]; then echo "bws: invalid access token" >&2; exit 1; fi
                                         case "$ID" in
                                           f1d3c8e5-5391-4aef-8611-b49d007c8702) cat "$DIR/key.pem"; exit 0 ;;
                                           wrong-key-secret-id) cat "$DIR/key2.pem"; exit 0 ;;
                                           garbage-secret-id) echo "definitely not an ssh private key"; exit 0 ;;
                                           sleep-secret-id) sleep 30; exit 0 ;;
                                           *) echo "bws: secret not found: $ID" >&2; exit 1 ;;
                                         esac
                                         """;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-bws-tests");
    private readonly string _fakeBws;

    public EncryptionBitwardenIntegrationTests()
    {
        var fakeDir = Path.Combine(_dataRoot, "fake-bws");
        Directory.CreateDirectory(fakeDir);
        _fakeBws = Path.Combine(fakeDir, "bws");
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private void WriteSidecar(string secretId = SecretId) =>
        File.WriteAllText(SidecarPath(),
            $$"""{"source":"bitwarden","projectId":"{{ProjectId}}","secretId":"{{secretId}}"}""");

    private EncryptionKeyResolver Resolver() =>
        new(new EncryptionSourceSidecar(BankPath()),
            [new StubEnvProvider("env-passphrase"), new BitwardenEncryptionKeyProvider(new BitwardenCliSecretManager(_fakeBws))]);

    /// <summary>Resolver pinned to the derived key, independent of the sidecar (the old StubEnvProvider semantics).</summary>
    private EncryptionKeyResolver DerivedKeyResolver() => new(new FixedState(), [new StubEnvProvider(DerivedRawKey)]);

    /// <summary>Resolver pinned to one key with no legacy form — the env-source shape.</summary>
    private static EncryptionKeyResolver FixedKeyResolver(string key) => new(new FixedState(), [new StubEnvProvider(key)]);

    /// <summary>Writes the fake-bws script + key fixtures next to it (absolute-path executable; no PATH mutation).</summary>
    private void InstallFakeBws()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_fakeBws)!, "key.pem"),
            BuildPem([.. Enumerable.Range(0, 32).Select(i => (byte)i)],
                [.. Enumerable.Range(1, 32).Select(i => (byte)i)]));
        // A second, different synthetic key (seed 0x20..0x3f) for the wrong-key leg.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_fakeBws)!, "key2.pem"),
            BuildPem([.. Enumerable.Range(0x20, 32).Select(i => (byte)i)],
                [.. Enumerable.Range(0x21, 32).Select(i => (byte)i)]));
        File.WriteAllText(_fakeBws, FakeBwsScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_fakeBws, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Runs body with BWS_ACCESS_TOKEN set to token (null = removed); restores the previous value.</summary>
    private static async Task WithBwsAccessToken(string? token, Func<Task> body)
    {
        var previous = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", token);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previous);
        }
    }

    [Fact]
    public async Task EnvKeyedBank_RekeyedToDerivedKey_ReopensThroughFakeBwsWithDerivedKey()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // 1. Create the bank keyed with the env passphrase (no sidecar → env is the source).
            var envFactory = new SqliteConnectionFactory(Options(), Resolver());
            await using (var connection = await envFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // 2. Rekey env → derived (the config command's rekey step; sidecar not yet written, so the
            //    resolver still resolves the env passphrase as the current key).
            var rekeyFactory = new SqliteConnectionFactory(Options(), Resolver());
            await rekeyFactory.RekeyBankAsync(DerivedRawKey, TestContext.Current.CancellationToken);

            // 3. The config command then persists the sidecar.
            WriteSidecar();

            // 4. Every later open resolves through the fake bws: real child process → provider → derived key.
            var resolverFactory = new SqliteConnectionFactory(Options(), Resolver());
            await using (var reopen = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                reopen.State.ShouldBe(ConnectionState.Open);
                await using var check = reopen.CreateCommand();
                check.CommandText = "SELECT count(*) FROM t";
                (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
            }

            // 5. Reopen again — the derived key keeps opening the bank.
            await using var again = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            again.State.ShouldBe(ConnectionState.Open);
        });
    }

    /// <summary>
    ///     The ADR-0012 migration gate: a bank encrypted under the pre-ADR-0012 derivation is
    ///     rekeyed to the HKDF key, keeps its data, and stops opening under the old key.
    /// </summary>
    [Fact]
    public async Task LegacyKeyedBank_Migrate_RekeysToHkdfKeyAndLegacyKeyStopsWorking()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // An installed user's bank: encrypted with SHA-256(Label ‖ seed), holding real data.
            var legacyFactory = new SqliteConnectionFactory(Options(), FixedKeyResolver(LegacyDerivedRawKey));
            await using (var connection = await legacyFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO t VALUES (1, 'survives')";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // The sidecar says bitwarden, so the resolver derives both keys from the same seed.
            WriteSidecar();
            var factory = new SqliteConnectionFactory(Options(), Resolver());

            await factory.MigrateLegacyKeyAsync(TestContext.Current.CancellationToken);

            // Readable under the HKDF key, with the row intact.
            await using (var reopened = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var read = reopened.CreateCommand();
                read.CommandText = "SELECT value FROM t WHERE id = 1";
                (await read.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("survives");
            }

            // And no longer readable under the legacy key — without this half, a bank that was
            // never encrypted at all would satisfy the assertion above.
            var stale = await Should.ThrowAsync<SqliteException>(async () =>
            {
                await using var conn = await factory.OpenBankWithKeyAsync(LegacyDerivedRawKey, TestContext.Current.CancellationToken);
            });
            stale.SqliteErrorCode.ShouldBe(26);
        });
    }

    /// <summary>
    ///     Decision 2 in code: a file that opens under neither derivation is refused, not rekeyed
    ///     over. The byte comparison is the real assertion — the exception type alone would not
    ///     prove that nothing was written.
    /// </summary>
    [Fact]
    public async Task CorruptBank_Migrate_RefusesAndLeavesTheFileByteIdentical()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // Deterministic garbage: a valid-looking size, no valid SQLCipher page 1.
            Directory.CreateDirectory(Path.GetDirectoryName(BankPath())!);
            File.WriteAllBytes(BankPath(), [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 % 251))]);
            var before = await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken);

            WriteSidecar();
            var factory = new SqliteConnectionFactory(Options(), Resolver());

            await Should.ThrowAsync<BankKeyMismatchException>(
                async () => await factory.MigrateLegacyKeyAsync(TestContext.Current.CancellationToken));

            (await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken)).ShouldBe(before);
        });
    }

    /// <summary>
    ///     Decision 3 pinned: the automatic open path detects and refuses, naming the verb. If
    ///     someone later makes the open path rekey by itself, this test is what fails.
    /// </summary>
    [Fact]
    public async Task OpenBankAsync_LegacyKeyedBank_RefusesWithMigrateGuidanceAndDoesNotRekey()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            var legacyFactory = new SqliteConnectionFactory(Options(), FixedKeyResolver(LegacyDerivedRawKey));
            await using (await legacyFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
            }

            WriteSidecar();
            var factory = new SqliteConnectionFactory(Options(), Resolver());

            var ex = await Should.ThrowAsync<BankKeyMismatchException>(async () =>
            {
                await using var conn = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            });

            ex.Message.ShouldContain("ai-raccoon encryption migrate");
            ex.InnerException.ShouldBeOfType<SqliteException>().SqliteErrorCode.ShouldBe(26);

            // The bank was not touched: the legacy key still opens it.
            await using var untouched = await legacyFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            untouched.State.ShouldBe(ConnectionState.Open);
        });
    }

    /// <summary>The env source has no legacy derivation, so its failure must surface unchanged.</summary>
    [Fact]
    public async Task OpenBankAsync_EnvSourceWrongPassphrase_RethrowsTheOriginalSqliteException()
    {
        var createFactory = new SqliteConnectionFactory(Options(), FixedKeyResolver("passphrase-a"));
        await using (await createFactory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        // No sidecar → env source, and the env provider never carries a legacy key.
        var wrongFactory = new SqliteConnectionFactory(Options(), FixedKeyResolver("passphrase-b"));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await wrongFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });

        ex.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task BankKeyedWithKeyA_FakeBwsServesDifferentKey_OpenFailsWithKeyMismatch()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // Bank keyed with the synthetic key (the §5.1 vector).
            var createFactory = new SqliteConnectionFactory(Options(), DerivedKeyResolver());
            await using (var connection = await createFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // The fake bws now serves key2.pem — a valid key that derives to something else. Neither
            // that key nor its legacy form opens the bank, so this is the same refusal a corrupt
            // bank gets: we cannot tell the two apart, and refusing is safe for both.
            WriteSidecar(WrongKeySecretId);
            var resolverFactory = new SqliteConnectionFactory(Options(), Resolver());

            var ex = await Should.ThrowAsync<BankKeyMismatchException>(async () =>
            {
                await using var conn = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            });

            ex.InnerException.ShouldBeOfType<SqliteException>().SqliteErrorCode.ShouldBe(26);
        });
    }

    [Fact]
    public void FakeBwsMissing_ResolverThrowsInstallGuidance()
    {
        // No InstallFakeBws: the runner points at an absolute path that does not exist.
        WriteSidecar();

        var ex = Should.Throw<BwsInvocationException>(() => Resolver().Resolve().Passphrase);

        ex.Message.ShouldBe(
            "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)");
    }

    [Fact]
    public async Task FakeBwsExitsNonZero_ResolverThrowsBwsFailedWithStderr()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            WriteSidecar(UnknownSecretId);

            var ex = Should.Throw<BwsInvocationException>(() => Resolver().Resolve().Passphrase);

            ex.Message.ShouldBe($"bws failed (exit 1): bws: secret not found: {UnknownSecretId}");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FakeBwsSleepsPastTimeout_RunnerThrowsTimeoutText()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            var ex = Should.Throw<BwsInvocationException>(() => new BitwardenCliSecretManager(_fakeBws).Run(["secret", "get", SleepSecretId], null, TimeSpan.FromSeconds(2)));

            ex.Message.ShouldBe("bws timed out after 2s");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TokenFromBwsAccessTokenEnv_ResolvesAndOpensBank()
    {
        InstallFakeBws();
        await WithBwsAccessToken(KnownToken, async () =>
        {
            WriteSidecar();

            var factory = new SqliteConnectionFactory(Options(), Resolver());

            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

            connection.State.ShouldBe(ConnectionState.Open);
        });
    }

    [Fact]
    public void TokenViaDashTArgv_FakeServesTheSecretKey()
    {
        InstallFakeBws();

        var result = new BitwardenCliSecretManager(_fakeBws)
            .Run(["secret", "get", SecretId], KnownToken, TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe(BuildPem(
            [.. Enumerable.Range(0, 32).Select(i => (byte)i)],
            [.. Enumerable.Range(1, 32).Select(i => (byte)i)]));
    }

    [Fact]
    public async Task BadToken_FakeBwsStderrSurfacedThroughResolver()
    {
        InstallFakeBws();
        await WithBwsAccessToken("definitely-wrong-token", async () =>
        {
            WriteSidecar();

            var ex = Should.Throw<BwsInvocationException>(() => Resolver().Resolve().Passphrase);

            ex.Message.ShouldBe("bws failed (exit 1): bws: invalid access token");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FakeBwsEmitsGarbage_BankUnchangedAndMalformedErrorSurfaced()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // Bank keyed with the derived key (as if the config command had completed).
            var createFactory = new SqliteConnectionFactory(Options(), DerivedKeyResolver());
            await using (var connection = await createFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // The sidecar now points at a secret whose value is not a key.
            WriteSidecar(GarbageSecretId);
            var resolverFactory = new SqliteConnectionFactory(Options(), Resolver());

            var ex = await Should.ThrowAsync<MalformedPrivateKeyException>(async () =>
            {
                await using var conn = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            });

            ex.Message.ShouldStartWith("malformed OpenSSH private key: ");

            // The bank is untouched: it still opens with the derived key (the create factory's
            // env stub resolves it — the sidecar now points at bws, so the shared resolver
            // would go through the fake).
            await using var untouched = await createFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            untouched.State.ShouldBe(ConnectionState.Open);
        });
    }

    [Fact]
    public async Task Startup_BwsMissing_Exits1WithResolveError()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the child launches a shell-based fake; the PATH override is unix-shaped
        }

        WriteSidecar();
        var emptyPathDir = Path.Combine(_dataRoot, "empty-path");
        Directory.CreateDirectory(emptyPathDir);

        var (exit, stderr, stdout) = await RunServerProcessAsync(emptyPathDir);

        exit.ShouldBe(ExitCode.FailedToResolveEncryptionKey);
        stderr.Contains("Failed to resolve encryption key", StringComparison.Ordinal)
            .ShouldBeTrue($"stderr='{stderr}' stdout='{stdout}'");
    }

    [Fact]
    public async Task Startup_BwsMissing_StillLogsSqliteEngineVersion()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the child launches a shell-based fake; the PATH override is unix-shaped
        }

        WriteSidecar();
        var emptyPathDir = Path.Combine(_dataRoot, "empty-path");
        Directory.CreateDirectory(emptyPathDir);

        var (exit, stderr, stdout) = await RunServerProcessAsync(emptyPathDir);

        exit.ShouldBe(ExitCode.FailedToResolveEncryptionKey);
        // Diagnostics fire before key resolution, so the engine identity is visible even
        // when startup fails (engine train was the 2.1.11 → 2.4.0 incident class).
        stderr.Contains("SQLite engine 3.53", StringComparison.Ordinal)
            .ShouldBeTrue($"stderr='{stderr}' stdout='{stdout}'");
        stderr.Contains("SQLite3 Multiple Ciphers 2.4", StringComparison.Ordinal)
            .ShouldBeTrue($"stderr='{stderr}' stdout='{stdout}'");
    }

    [Fact]
    public async Task Startup_WrongKey_Exits2WithOpenError()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the child launches a shell-based fake; the PATH override is unix-shaped
        }

        InstallFakeBws();
        // Bank keyed with a passphrase that is NOT in the child's environment; the sidecar then
        // routes the child's resolve through the fake bws, which serves a key that derives to
        // something else → the probe open fails with SQLCipher code 26 → exit 2.
        var envFactory = new SqliteConnectionFactory(Options(), new EncryptionKeyResolver(
            new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider("env-passphrase")]));
        await using (await envFactory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        WriteSidecar();
        var (exit, stderr, stdout) = await RunServerProcessAsync(Path.GetDirectoryName(_fakeBws)!);

        exit.ShouldBe(ExitCode.FailedToOpenEncryptedBank);
        stderr.Contains("Failed to open encrypted bank with bitwarden encryption source key", StringComparison.Ordinal)
            .ShouldBeTrue($"stderr='{stderr}' stdout='{stdout}'");
    }

    /// <summary>Runs the real server binary with a hermetic PATH rooted at bwsDir (an empty
    /// dir = bws missing, the fake-bws dir = fake resolves) plus the system dirs the fake
    /// script needs; returns the exit code and both streams. Program.cs's error mapping and
    /// exit codes 1/2 are only exercised by this process-level path.</summary>
    private async Task<(int Exit, string Stderr, string Stdout)> RunServerProcessAsync(string bwsDir)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "AiRaccoon.dll");
        File.Exists(dll).ShouldBeTrue($"AiRaccoon.dll not found at {dll}");

        var dotnetPath = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
                             .Select(dir => Path.Combine(dir, "dotnet"))
                             .FirstOrDefault(File.Exists)
                         ?? throw new InvalidOperationException("dotnet not found on PATH");

        var psi = new ProcessStartInfo(dotnetPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _dataRoot
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--data-root");
        psi.ArgumentList.Add(_dataRoot);
        // Hermetic: the child sees only the fake dir + /usr/bin + /bin — a real bws on the
        // dev machine's PATH can never leak into the run (the fake script needs cat/sh).
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, [bwsDir, "/usr/bin", "/bin"]);
        psi.Environment["BWS_ACCESS_TOKEN"] = "";
        psi.Environment[EnvEncryptionKeyProvider.EnvVarName] = "";

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await stderrTask, await stdoutTask);
    }

    /// <summary>Assembles an unencrypted ed25519 openssh-key-v1 PEM from synthetic bytes — deterministic, no real key material.</summary>
    private static string BuildPem(byte[] seed, byte[] pub)
    {
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

        var base64 = Convert.ToBase64String(body.ToArray());
        var wrapped = string.Join('\n', Enumerable.Range(0, (base64.Length + 63) / 64)
            .Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64))));
        return $"-----BEGIN OPENSSH PRIVATE KEY-----\n{wrapped}\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static void WriteUInt32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Passphrase GetPassphrase(EncryptionData encryptionData) => new(Source) { Value = passphrase };
    }

    /// <summary>Never sees a sidecar — the resolver behaves like the pre-sidecar env stub.</summary>
    private sealed class FixedState : IEncryptionSourceSidecar
    {
        public string FilePath => "";
        public EncryptionData Read() => EncryptionData.None;
        public void Write(EncryptionData config) { }
        public void Delete() { }
    }
}
