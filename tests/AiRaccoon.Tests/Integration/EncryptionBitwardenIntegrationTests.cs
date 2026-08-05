using System.Data;
using System.Text;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     End-to-end through the real stack: a fake `bws` shell script at an absolute path (no PATH
///     mutation) → BwsProcessRunner → BitwardenEncryptionKeyProvider → EncryptionKeyResolver →
///     SqliteConnectionFactory against real temp SQLCipher banks (plan §S4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EncryptionBitwardenIntegrationTests : IDisposable
{
    // §5.1 pinned vector — seed 00 01 … 1e 1f derives to exactly this x'…' (hard-coded, never recomputed).
    private const string DerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    // Owner-default project/secret ids (plan D6). The fake bws serves the synthetic key for SecretId.
    private const string ProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string SecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";
    private const string WrongKeySecretId = "wrong-key-secret-id"; // fake serves key2.pem → a different derived key
    private const string GarbageSecretId = "garbage-secret-id";    // fake prints non-key stdout
    private const string SleepSecretId = "sleep-secret-id";        // fake sleeps (timeout leg)
    private const string UnknownSecretId = "unknown-secret-id";    // fake exits 1 "secret not found"

    // Obviously fake; the fake bws accepts exactly this token, from BWS_ACCESS_TOKEN or argv -t.
    private const string KnownToken = "test-bws-token-0123";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-bws-tests");
    private readonly string _fakeBws;

    public EncryptionBitwardenIntegrationTests()
    {
        var fakeDir = Path.Combine(_dataRoot, "fake-bws");
        Directory.CreateDirectory(fakeDir);
        _fakeBws = Path.Combine(fakeDir, "bws");
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() =>
        new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private void WriteSidecar(string secretId = SecretId) =>
        File.WriteAllText(SidecarPath(),
            $$"""{"source":"bitwarden","projectId":"{{ProjectId}}","secretId":"{{secretId}}"}""");

    private EncryptionKeyResolver Resolver() =>
        new(Options(), new StubEnvProvider("env-passphrase"), new BwsProcessRunner(_fakeBws));

    /// <summary>Writes the fake-bws script + key fixtures next to it (absolute-path executable; no PATH mutation).</summary>
    private void InstallFakeBws()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_fakeBws)!, "key.pem"),
            BuildPem(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        // A second, different synthetic key (seed 0x20..0x3f) for the wrong-key leg.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_fakeBws)!, "key2.pem"),
            BuildPem(Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray(),
                Enumerable.Range(0x21, 32).Select(i => (byte)i).ToArray()));
        File.WriteAllText(_fakeBws, FakeBwsScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_fakeBws, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

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
            var envFactory = new SqliteConnectionFactory(Options(), new StubEnvProvider("env-passphrase"));
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

    [Fact]
    public async Task BankKeyedWithKeyA_FakeBwsServesDifferentKey_OpenFailsWithSqliteCode26()
    {
        InstallFakeBws();
        await WithBwsAccessToken(null, async () =>
        {
            // Bank keyed with the synthetic key (the §5.1 vector).
            var createFactory = new SqliteConnectionFactory(Options(), new StubEnvProvider(DerivedRawKey));
            await using (var connection = await createFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // The fake bws now serves key2.pem — a valid key that derives to something else.
            WriteSidecar(WrongKeySecretId);
            var resolverFactory = new SqliteConnectionFactory(Options(), Resolver());

            var ex = await Should.ThrowAsync<SqliteException>(async () =>
            {
                await using var conn = await resolverFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            });

            ex.SqliteErrorCode.ShouldBe(26);
        });
    }

    [Fact]
    public void FakeBwsMissing_ResolverThrowsInstallGuidance()
    {
        // No InstallFakeBws: the runner points at an absolute path that does not exist.
        WriteSidecar();

        var ex = Should.Throw<BwsInvocationException>(() => Resolver().GetPassphrase());

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

            var ex = Should.Throw<BwsInvocationException>(() => Resolver().GetPassphrase());

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
            var ex = Should.Throw<BwsInvocationException>(
                () => new BwsProcessRunner(_fakeBws).Run(["secret", "get", SleepSecretId], null, TimeSpan.FromSeconds(2)));

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

        var result = new BwsProcessRunner(_fakeBws)
            .Run(["secret", "get", SecretId], KnownToken, TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe(BuildPem(
            Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
    }

    [Fact]
    public async Task BadToken_FakeBwsStderrSurfacedThroughResolver()
    {
        InstallFakeBws();
        await WithBwsAccessToken("definitely-wrong-token", async () =>
        {
            WriteSidecar();

            var ex = Should.Throw<BwsInvocationException>(() => Resolver().GetPassphrase());

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
            var createFactory = new SqliteConnectionFactory(Options(), new StubEnvProvider(DerivedRawKey));
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

            // The bank is untouched: it still opens with the derived key.
            await using var untouched = await createFactory.OpenBankAsync(TestContext.Current.CancellationToken);
            untouched.State.ShouldBe(ConnectionState.Open);
        });
    }

    /// <summary>Assembles an unencrypted ed25519 openssh-key-v1 PEM from synthetic bytes — deterministic, no real key material.</summary>
    private static string BuildPem(byte[] seed, byte[] pub)
    {
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        WriteString(body, "none");
        WriteString(body, "none");
        WriteString(body, Array.Empty<byte>());
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
        WriteString(priv, seed.Concat(pub).ToArray());
        WriteString(priv, Array.Empty<byte>());
        priv.Write(new byte[8 - ((int)priv.Length % 8)]);
        WriteString(body, priv.ToArray());

        var base64 = Convert.ToBase64String(body.ToArray());
        var wrapped = string.Join('\n', Enumerable.Range(0, (base64.Length + 63) / 64)
            .Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64))));
        return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + wrapped + "\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static void WriteUInt32(Stream stream, uint value) =>
        stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string? GetPassphrase() => passphrase;
    }
}
