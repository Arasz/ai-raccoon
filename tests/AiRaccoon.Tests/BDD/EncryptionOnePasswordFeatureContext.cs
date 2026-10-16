using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for the encryption-1password feature scenarios — one instance per scenario.
///     Real SqliteConnectionFactory + SqliteMemoryStore over an EncryptionKeyResolver whose op leg
///     is a fake `op` script at an absolute path (no PATH mutation).
/// </summary>
public sealed class EncryptionOnePasswordFeatureContext : MemoryFeatureContext
{
    // Derived from seed 00..1f (hard-coded, never recomputed).
    public const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    public const string Reference = "op://vault/item/private key?ssh-format=openssh";

    /// <summary>The env leg of the resolver: a fixed stub passphrase (never the ambient environment).</summary>
    public const string EnvPassphrase = "env-passphrase";

    // fake op: serves the key fixture for the one known reference, and logs every invocation with
    // the inherited OP_SERVICE_ACCOUNT_TOKEN so steps can assert on both argv and environment.
    private const string FakeOpScript = """
                                        #!/bin/sh
                                        DIR="$(dirname "$0")"
                                        echo "$* | env:OP_SERVICE_ACCOUNT_TOKEN=${OP_SERVICE_ACCOUNT_TOKEN}" >> "$DIR/op-calls.log"
                                        case "$1" in
                                          --version) echo "2.30.0 (fake)"; exit 0 ;;
                                          read)
                                            case "$2" in
                                              "op://vault/item/private key?ssh-format=openssh") cat "$DIR/key.pem"; exit 0 ;;
                                              *) echo "[ERROR] \"$2\" isn't an item in any vault" >&2; exit 1 ;;
                                            esac ;;
                                        esac
                                        echo "op: unknown command" >&2
                                        exit 1
                                        """;

    public EncryptionOnePasswordFeatureContext()
    {
        var options = new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        FakeOpDir = Path.Combine(DataRoot, "fake-op");
        OpExecutable = Path.Combine(FakeOpDir, "op");
        var runner = new PathSwitchingRunner(() => OpExecutable);
        OnePasswordBank = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new StubEnvProvider(EnvPassphrase), new OnePasswordEncryptionKeyProvider(runner)]));
        ConfigStore = TestData.CreateMemoryStore(OnePasswordBank, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(OnePasswordBank), new StubChunker(), TimeProvider, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public string FakeOpDir { get; }

    /// <summary>Absolute path the runner invokes; "op is not installed" points it at a path that never exists.</summary>
    public string OpExecutable { get; set; }

    public SqliteConnectionFactory OnePasswordBank { get; }

    /// <summary>Real store over the bank — the store the config commands write through.</summary>
    public SqliteMemoryStore ConfigStore { get; }

    public string BankPath => OnePasswordBank.BankPath;

    public string SidecarPath => EncryptionSourceSidecar.PathFor(BankPath);

    public string CallsLogPath => Path.Combine(FakeOpDir, "op-calls.log");

    /// <summary>Writes the fake op script and the synthetic ed25519 fixture, and makes the script executable.</summary>
    public void InstallFakeOp()
    {
        Directory.CreateDirectory(FakeOpDir);
        File.WriteAllText(Path.Combine(FakeOpDir, "key.pem"), new TestOpenSshKeyBuilder().Build());
        File.WriteAllText(OpExecutable, FakeOpScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(OpExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The feature's "the encryption source is 1password" baseline: fake installed, sidecar written, bank keyed through the resolver, settings rows persisted.</summary>
    public async Task ConfigureOnePasswordSourceAsync(CancellationToken cancellationToken = default)
    {
        InstallFakeOp();
        new EncryptionSourceSidecar(BankPath).Write(new EncryptionData("1password") { SecretId = Reference });
        await using (await OnePasswordBank.OpenBankAsync(cancellationToken))
        {
        }

        await ConfigStore.SetSettingAsync(EncryptionSettingsKeys.Source, "1password", cancellationToken);
        await ConfigStore.SetSettingAsync(EncryptionSettingsKeys.SecretId, Reference, cancellationToken);
    }

    /// <summary>Runs one CLI config command in-process (the same dispatch as Program.cs) and returns its exit code and output.</summary>
    public async Task<CliOutcome> RunCliAsync(string stdin, params string[] args)
    {
        var commands = new EncryptionCommands(OnePasswordBank, new CliSecretManager(CliSecretTool.Bitwarden, Path.Combine(DataRoot, "no-bws")), NewRunner(),
            new StubEnvProvider(EnvPassphrase), new EncryptionSourceSidecar(BankPath), new FakeLogger<EncryptionCommands>());
        var (exit, stdout, stderr) = await CliRun.RunAsync(args,
            TestData.CreateConfigCommands(ConfigStore, encryptionCommands: commands),
            new StringReader(stdin));
        return new CliOutcome(exit, stdout, stderr);
    }

    /// <summary>Resolves the key through a fresh, cold-cache resolver (a server start) and opens the bank with it; null on success, else the failure text.</summary>
    public async Task<string?> StartServerErrorAsync(CancellationToken cancellationToken = default)
    {
        var freshResolver = new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath),
            [new StubEnvProvider(EnvPassphrase), new OnePasswordEncryptionKeyProvider(NewRunner())]);
        try
        {
            var resolved = await freshResolver.ResolveAsync(cancellationToken);
            await using var probe = await OnePasswordBank.OpenBankWithKeyAsync(resolved.Passphrase, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public ICliSecretManager NewRunner() => new CliSecretManager(CliSecretTool.OnePassword, OpExecutable);

    /// <summary>Outcome of one in-process CLI run.</summary>
    public sealed record CliOutcome(int Exit, string Out, string Err);

    private sealed class PathSwitchingRunner(Func<string> path) : ICliSecretManager
    {
        public Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            new CliSecretManager(CliSecretTool.OnePassword, path()).RunAsync(args, token, timeout, cancellationToken);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) => Task.FromResult(new Passphrase(Source) { Value = passphrase });
    }
}
