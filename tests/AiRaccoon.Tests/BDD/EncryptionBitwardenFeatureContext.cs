using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for the encryption-bitwarden feature scenarios — one instance per scenario.
///     Real SqliteConnectionFactory + SqliteMemoryStore over an EncryptionKeyResolver whose bws
///     leg is a fake `bws` script at an absolute path (no PATH mutation, no ambient-env reads).
/// </summary>
public sealed class EncryptionBitwardenFeatureContext : MemoryFeatureContext
{
    // Derived from seed 00..1f (hard-coded, never recomputed).
    public const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    // Matches EncryptionCommands' fallback placeholder ids (no env override configured), so
    // scenarios that accept the interactive default via empty stdin still resolve through the fake.
    public const string ProjectId = "00000000-0000-0000-0000-000000000000";
    public const string SecretId = "11111111-1111-1111-1111-111111111111";
    public const string WrongKeySecretId = "wrong-key-secret-id"; // serves key2.pem → a different derived key
    public const string BcryptSecretId = "bcrypt-secret-id"; // serves a passphrase-protected key
    public const string RsaSecretId = "rsa-secret-id"; // serves an ssh-rsa key
    public const string UnreachableSecretId = "unreachable-secret-id"; // fake exits 1 "connection refused"

    // Obviously fake; the fake bws accepts exactly this token via argv -t and rejects any other.
    public const string KnownToken = "test-bws-token-0123";

    /// <summary>The env leg of the resolver: a fixed stub passphrase (never the ambient environment).</summary>
    public const string EnvPassphrase = "env-passphrase";

    // fake bws: serves the matching key fixture per secret id, accepts the known token from either
    // channel, and logs every invocation to bws-calls.log for steps to assert against. Each line
    // records argv AND the inherited BWS_ACCESS_TOKEN, because the token travels by environment
    // rather than argv — that is the property the -t scenario now pins.
    private const string FakeBwsScript = """
                                         #!/bin/sh
                                         DIR="$(dirname "$0")"
                                         echo "$@ | env:BWS_ACCESS_TOKEN=${BWS_ACCESS_TOKEN}" >> "$DIR/bws-calls.log"
                                         TOKEN=""
                                         ID=""
                                         while [ "$#" -gt 0 ]; do
                                           case "$1" in
                                             -t) TOKEN="$2"; shift 2 ;;
                                             --version) echo "bws 1.0.0 (fake)"; exit 0 ;;
                                             get) ID="$2"; shift 2 ;;
                                             *) shift ;;
                                           esac
                                         done
                                         TOKEN="${TOKEN:-$BWS_ACCESS_TOKEN}"
                                         if [ -z "$ID" ]; then echo "bws: missing secret id" >&2; exit 1; fi
                                         if [ -n "$TOKEN" ] && [ "$TOKEN" != "test-bws-token-0123" ]; then echo "bws: invalid access token" >&2; exit 1; fi
                                         case "$ID" in
                                           11111111-1111-1111-1111-111111111111) cat "$DIR/key.pem"; exit 0 ;;
                                           custom-secret-id) cat "$DIR/key.pem"; exit 0 ;;
                                           wrong-key-secret-id) cat "$DIR/key2.pem"; exit 0 ;;
                                           bcrypt-secret-id) cat "$DIR/key-bcrypt.pem"; exit 0 ;;
                                           rsa-secret-id) cat "$DIR/key-rsa.pem"; exit 0 ;;
                                           unreachable-secret-id) echo "bws: error: request failed (connection refused)" >&2; exit 1 ;;
                                           garbage-secret-id) echo "definitely not an ssh private key"; exit 0 ;;
                                           *) echo "bws: secret not found: $ID" >&2; exit 1 ;;
                                         esac
                                         """;

    private static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly byte[] PublicKey01To20 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    public EncryptionBitwardenFeatureContext()
    {
        var options = new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        FakeBwsDir = Path.Combine(DataRoot, "fake-bws");
        BwsExecutable = Path.Combine(FakeBwsDir, "bws");
        var runner = new PathSwitchingRunner(() => BwsExecutable);
        Resolver = new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
            [new StubEnvProvider(EnvPassphrase), new BitwardenEncryptionKeyProvider(runner)]);
        Bank = new SqliteConnectionFactory(options, Resolver);
        ConfigStore = TestData.CreateMemoryStore(Bank, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(Bank), new StubChunker(), TimeProvider, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    /// <summary>Directory holding the fake bws script + key fixtures (installed lazily by <see cref="InstallFakeBws"/>).</summary>
    public string FakeBwsDir { get; }

    /// <summary>
    ///     Absolute path the runner invokes. "bws is installed" points it at the fake script;
    ///     "bws is not installed" points it at a path that never exists (install-guidance error).
    /// </summary>
    public string BwsExecutable { get; set; }

    public EncryptionKeyResolver Resolver { get; }

    /// <summary>Real factory whose key provider resolves the source (sidecar → bws/env) on every open.</summary>
    public SqliteConnectionFactory Bank { get; }

    /// <summary>
    ///     Real store over <see cref="Bank"/> — the store the config commands write through, so
    ///     settings writes open the bank under the current encryption source (Program.cs wiring).
    /// </summary>
    public SqliteMemoryStore ConfigStore { get; }

    public string BankPath => Bank.BankPath;

    public string SidecarPath => EncryptionSourceSidecar.PathFor(BankPath);

    public string CallsLogPath => Path.Combine(FakeBwsDir, "bws-calls.log");

    /// <summary>
    ///     Writes the fake bws script + the synthetic ed25519 fixtures and makes the script
    ///     executable. Absolute path; no PATH mutation.
    /// </summary>
    public void InstallFakeBws()
    {
        Directory.CreateDirectory(FakeBwsDir);
        WriteKeyFixture("key.pem", BuildEd25519Pem(Seed00To1F, PublicKey01To20));
        WriteKeyFixture("key2.pem", BuildEd25519Pem(
            [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)],
            [.. Enumerable.Range(0x21, 32).Select(i => (byte)i)]));
        File.WriteAllText(Path.Combine(FakeBwsDir, "bws"), FakeBwsScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(FakeBwsDir, "bws"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Writes a fixture file next to the fake bws script.</summary>
    public void WriteKeyFixture(string fileName, string pem) => File.WriteAllText(Path.Combine(FakeBwsDir, fileName), pem);

    /// <summary>Unencrypted ed25519 pem carrying the vector seed (00..1f).</summary>
    public string BuildVectorPem() => BuildEd25519Pem(Seed00To1F, PublicKey01To20);

    /// <summary>Passphrase-protected (aes256-ctr + bcrypt) ed25519 pem — must be rejected by the parser.</summary>
    public string BuildEncryptedPem() => new TestOpenSshKeyBuilder().WithEncrypted().Build();

    /// <summary>ssh-rsa pem — must be rejected by the parser (only ed25519 is supported).</summary>
    public string BuildRsaPem() => new TestOpenSshKeyBuilder().WithKeyType("ssh-rsa").Build();

    /// <summary>Writes the sidecar as the bitwarden source pointing at the given secret id.</summary>
    public void WriteSidecar(string secretId, string projectId = ProjectId) =>
        new EncryptionSourceSidecar(BankPath).Write(new EncryptionData("bitwarden") { ProjectId = projectId, SecretId = secretId });

    /// <summary>
    ///     The feature's "the encryption source is bitwarden" baseline: fake installed, sidecar
    ///     written, bank created through the resolver (so it is keyed with the derived key), and
    ///     the settings mirror rows persisted through the real store.
    /// </summary>
    public async Task ConfigureBitwardenSourceAsync(CancellationToken cancellationToken = default)
    {
        InstallFakeBws();
        WriteSidecar(SecretId);
        await using (await Bank.OpenBankAsync(cancellationToken))
        {
        }

        await ConfigStore.SetSettingAsync(EncryptionSettingsKeys.Source, "bitwarden", cancellationToken);
        await ConfigStore.SetSettingAsync(EncryptionSettingsKeys.ProjectId, ProjectId, cancellationToken);
        await ConfigStore.SetSettingAsync(EncryptionSettingsKeys.SecretId, SecretId, cancellationToken);
    }

    /// <summary>Creates the bank keyed with the env passphrase (no sidecar → env is the source).</summary>
    public async Task CreateEnvKeyedBankAsync(CancellationToken cancellationToken = default)
    {
        await using var probe = await Bank.OpenBankWithKeyAsync(EnvPassphrase, cancellationToken);
    }

    /// <summary>
    ///     Runs one CLI config command in-process (CliArgs.Parse + ConfigCommands.RunAsync, the
    ///     same dispatch as Program.cs) and returns its exit code plus stdout/stderr. Throws if
    ///     the command does not parse.
    /// </summary>
    public async Task<CliRun> RunCliAsync(string stdin, params string[] args)
    {
        var envProvider = new StubEnvProvider(EnvPassphrase);
        var encryptionState = new EncryptionSourceSidecar(BankPath);
        var encryptionCommands = new EncryptionCommands(Bank, NewRunner(), envProvider, encryptionState, new FakeLogger<EncryptionCommands>());
        var (exit, stdout, stderr) = await TestHelpers.CliRun.RunAsync(args,
            TestData.CreateConfigCommands(ConfigStore, encryptionCommands: encryptionCommands),
            new StringReader(stdin));
        return new CliRun(exit, stdout, stderr);
    }

    /// <summary>
    ///     Mirrors the Program.cs eager-startup open: resolves the key then opens the bank. Uses a
    ///     fresh resolver, not the scenario's shared <see cref="Resolver" /> — a real server process
    ///     gets a fresh, cold-cache resolver from DI on every start (.NET-F2 caches per resolver
    ///     instance, not globally), and the "Given" step's own fixture probe-open must not poison
    ///     what "the server opens the bank" observes. Returns null on success, else the error text
    ///     the process would print (mismatch text only for an actual key mismatch, SQLCipher code
    ///     26; other errors map generically).
    /// </summary>
    public async Task<string?> StartServerErrorAsync(CancellationToken cancellationToken = default)
    {
        var freshResolver = new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath),
            [new StubEnvProvider(EnvPassphrase), new BitwardenEncryptionKeyProvider(NewRunner())]);

        ResolvedKey resolved;
        try
        {
            resolved = await freshResolver.ResolveAsync(cancellationToken);
        }
        catch (Exception)
        {
            return "Failed to resolve encryption key";
        }

        try
        {
            await using var probe = await Bank.OpenBankWithKeyAsync(resolved.Passphrase, cancellationToken);
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            return $"Failed to open encrypted bank with {resolved.SourceName} encryption source key: {ex.Message}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public ICliSecretManager NewRunner() => new BitwardenCliSecretManager(BwsExecutable);

    /// <summary>Assembles an unencrypted ed25519 openssh-key-v1 PEM from synthetic bytes — deterministic, no real key material.</summary>
    private static string BuildEd25519Pem(byte[] seed, byte[] pub) => new TestOpenSshKeyBuilder().Build(seed, pub);

    /// <summary>Outcome of one in-process CLI run (the same dispatch as Program.cs).</summary>
    public sealed record CliRun(int Exit, string Out, string Err);

    private sealed class PathSwitchingRunner(Func<string> path) : ICliSecretManager
    {
        public Task<BwsResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            new BitwardenCliSecretManager(path()).RunAsync(args, token, timeout, cancellationToken);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) => Task.FromResult(new Passphrase(Source) { Value = passphrase });
    }
}
