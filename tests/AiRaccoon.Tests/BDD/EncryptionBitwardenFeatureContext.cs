using System.Text;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for the encryption-bitwarden feature scenarios — one instance per scenario.
///     Real temp DataRoot, real SqliteConnectionFactory + SqliteMemoryStore over an
///     EncryptionKeyResolver whose env leg is a fixed stub passphrase and whose bws leg is a
///     fake `bws` shell script at an absolute path (no PATH mutation, no ambient-env reads —
///     the suite must not depend on the developer's AIRACCOON_DB_PASSPHRASE / BWS_ACCESS_TOKEN).
///     The fake serves synthetic key fixtures per secret id, validates the known test token,
///     and appends every invocation to bws-calls.log so steps can assert what the CLI ran.
/// </summary>
public sealed class EncryptionBitwardenFeatureContext : MemoryFeatureContext
{
    // §5.1 pinned vector — seed 00 01 … 1e 1f derives to exactly this x'…' (hard-coded, never recomputed).
    public const string DerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    // Owner-default project/secret ids (plan D6) and the synthetic fixtures the fake serves.
    public const string ProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    public const string SecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";
    public const string WrongKeySecretId = "wrong-key-secret-id"; // serves key2.pem → a different derived key
    public const string BcryptSecretId = "bcrypt-secret-id"; // serves a passphrase-protected key
    public const string RsaSecretId = "rsa-secret-id"; // serves an ssh-rsa key
    public const string UnreachableSecretId = "unreachable-secret-id"; // fake exits 1 "connection refused"

    // Obviously fake; the fake bws accepts exactly this token via argv -t and rejects any other.
    public const string KnownToken = "test-bws-token-0123";

    /// <summary>The env leg of the resolver: a fixed stub passphrase (never the ambient environment).</summary>
    public const string EnvPassphrase = "env-passphrase";

    // fake bws: `--version` succeeds (presence check); `secret get <id>` serves the matching
    // fixture; a token is accepted only via argv -t and must equal the known test token. The
    // inherited BWS_ACCESS_TOKEN is deliberately ignored — the env-token channel is S4's
    // concern, and the BDD suite must not depend on the ambient shell. Every invocation is
    // appended to bws-calls.log so steps can assert what the CLI actually ran.
    private const string FakeBwsScript = """
                                         #!/bin/sh
                                         DIR="$(dirname "$0")"
                                         echo "$@" >> "$DIR/bws-calls.log"
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
                                         if [ -z "$ID" ]; then echo "bws: missing secret id" >&2; exit 1; fi
                                         if [ -n "$TOKEN" ] && [ "$TOKEN" != "test-bws-token-0123" ]; then echo "bws: invalid access token" >&2; exit 1; fi
                                         case "$ID" in
                                           f1d3c8e5-5391-4aef-8611-b49d007c8702) cat "$DIR/key.pem"; exit 0 ;;
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
        Resolver = new EncryptionKeyResolver(new EncryptionState(SqliteConnectionFactory.BankPathFor(options)),
            [new StubEnvProvider(EnvPassphrase), new BitwardenEncryptionKeyProvider(runner)]);
        Bank = new SqliteConnectionFactory(options, Resolver);
        ConfigStore = new SqliteMemoryStore(Bank, TimeProvider, new StubChunker(), new EmbeddingService());
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

    public string SidecarPath => EncryptionState.PathFor(BankPath);

    public string CallsLogPath => Path.Combine(FakeBwsDir, "bws-calls.log");

    /// <summary>
    ///     Writes the fake bws script + the synthetic ed25519 fixtures (key.pem = §5.1 vector seed,
    ///     key2.pem = a different seed) and makes the script executable. Absolute path; no PATH mutation.
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

    /// <summary>Unencrypted ed25519 pem carrying the §5.1 vector seed (00..1f).</summary>
    public string BuildVectorPem() => BuildEd25519Pem(Seed00To1F, PublicKey01To20);

    /// <summary>Passphrase-protected (aes256-ctr + bcrypt) ed25519 pem — must be rejected by the parser.</summary>
    public string BuildEncryptedPem() => new OpenSshKeyBuilder().WithEncrypted().Build();

    /// <summary>ssh-rsa pem — must be rejected by the parser (only ed25519 is supported).</summary>
    public string BuildRsaPem() => new OpenSshKeyBuilder().WithKeyType("ssh-rsa").Build();

    /// <summary>Writes the sidecar as the bitwarden source pointing at the given secret id.</summary>
    public void WriteSidecar(string secretId, string projectId = ProjectId) =>
        new EncryptionState(BankPath).Write(new EncryptionData("bitwarden") { ProjectId = projectId, SecretId = secretId });

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
    ///     One real CLI config command in-process (CliArgs.Parse + ConfigCommands.RunAsync — the
    ///     same dispatch as Program.cs) against the scenario's resolver-backed bank/store, with
    ///     the fake bws runner and the given stdin (the interactive prompts). Returns exit code
    ///     plus stdout/stderr. A command that does not parse is a broken contract — fail loudly.
    /// </summary>
    public async Task<CliRun> RunCliAsync(string stdin, params string[] args)
    {
        CliArgs.TryParse(args, out var parsed);
        if (parsed.Errors.Count > 0 || parsed.CommandPath.Length == 0)
        {
            throw new InvalidOperationException(
                $"CLI command did not parse: {string.Join("; ", parsed.Errors)}");
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, ConfigStore, stdout, stderr,
            new StringReader(stdin), CancellationToken.None, Bank, NewRunner(), new StubEnvProvider(EnvPassphrase),
            watchStore: null, encryptionState: new EncryptionState(BankPath), logger: new FakeLogger());
        return new CliRun(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    ///     "The server opens the bank": the Program.cs eager-startup open (resolve the key, open
    ///     with it) with the same loud failure mapping — returns null on success, else the error
    ///     text the process would print (bws invocation errors verbatim; code-26 with a bitwarden
    ///     source → the encryption-mismatch message; code-26 with no source → the no-source message).
    /// </summary>
    public async Task<string?> StartServerErrorAsync(CancellationToken cancellationToken = default)
    {
        ResolvedKey resolved;
        try
        {
            resolved = Resolver.Resolve();
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
        catch (Exception)
        {
            return $"Failed to open encrypted bank with {resolved.SourceName} encryption source key";
        }
    }

    public ICliSecretManager NewRunner() => new BitwardenCliSecretManager(BwsExecutable);

    /// <summary>Assembles an unencrypted ed25519 openssh-key-v1 PEM from synthetic bytes — deterministic, no real key material.</summary>
    private static string BuildEd25519Pem(byte[] seed, byte[] pub) => new OpenSshKeyBuilder().Build(seed, pub);

    /// <summary>Outcome of one in-process CLI run (the same dispatch as Program.cs).</summary>
    public sealed record CliRun(int Exit, string Out, string Err);

    private sealed class PathSwitchingRunner(Func<string> path) : ICliSecretManager
    {
        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout) => new BitwardenCliSecretManager(path()).Run(args, token, timeout);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Passphrase GetPassphrase(EncryptionData encryptionData) => new(Source) { Value = passphrase };
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class OpenSshKeyBuilder
    {
        private string _cipherName = "none";
        private string _kdfName = "none";
        private string _keyType = "ssh-ed25519";

        public OpenSshKeyBuilder WithEncrypted(string cipherName = "aes256-ctr", string kdfName = "bcrypt")
        {
            _cipherName = cipherName;
            _kdfName = kdfName;
            return this;
        }

        public OpenSshKeyBuilder WithKeyType(string keyType)
        {
            _keyType = keyType;
            return this;
        }

        public string Build(byte[]? seed = null, byte[]? pub = null)
        {
            seed ??= Seed00To1F;
            pub ??= PublicKey01To20;

            using var body = new MemoryStream();
            body.Write("openssh-key-v1\0"u8);
            WriteString(body, _cipherName);
            WriteString(body, _kdfName);
            WriteString(body, []);
            WriteUInt32(body, 1);
            WriteString(body, BuildPublicKeyBlob(pub));
            WriteString(body, BuildPrivateSection(seed, pub));

            var base64 = Convert.ToBase64String(body.ToArray());
            var wrapped = string.Join('\n', Enumerable.Range(0, (base64.Length + 63) / 64)
                .Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64))));
            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + wrapped + "\n-----END OPENSSH PRIVATE KEY-----\n";
        }

        private byte[] BuildPublicKeyBlob(byte[] pub)
        {
            using var blob = new MemoryStream();
            WriteString(blob, _keyType);
            WriteString(blob, pub);
            return blob.ToArray();
        }

        private byte[] BuildPrivateSection(byte[] seed, byte[] pub)
        {
            using var section = new MemoryStream();
            WriteUInt32(section, 0x01234567);
            WriteUInt32(section, 0x01234567);
            WriteString(section, _keyType);
            WriteString(section, pub);
            WriteString(section, [.. seed, .. pub]);
            WriteString(section, []);
            section.Write(new byte[8 - (int)section.Length % 8]);
            return section.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteString(Stream stream, byte[] value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }
    }
}
