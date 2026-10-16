using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     encryption 1password/show/unset (ADR-0132): op presence with install guidance, the secret
///     reference prompt, `op://` validation before any fetch, the fetch through a fake `op` script at an
///     absolute path, and the shared rekey, sidecar and settings persist order.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(OpServiceAccountTokenCollection.Name)]
public sealed class ConfigCommandsOnePasswordTests : IDisposable
{
    // pinned vector: seed 00 01 … 1e 1f → x'72d2…' (TestOpenSshKeyBuilder builds that seed).
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    private const string Reference = "op://vault/item/private key?ssh-format=openssh";
    private const string FakePlaceholder = "op://example-vault/example-item/private key?ssh-format=openssh";

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly string _fakeDir;

    private FakeLogger<EncryptionCommands>? _lastLogger;

    public ConfigCommandsOnePasswordTests()
    {
        _fakeDir = Path.Combine(_dataRoot, "fake-op");
        Directory.CreateDirectory(_fakeDir);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private string ReadMarker() => Path.Combine(_fakeDir, "read.marker");

    private string Logged(string name) => File.ReadAllText(Path.Combine(_fakeDir, name));

    /// <summary>A fake `op` that answers --version, records what `op read` received, then runs <paramref name="readBody" />.</summary>
    private CliSecretManager FakeOp(string readBody)
    {
        var path = Path.Combine(_fakeDir, "op");
        File.WriteAllText(path, $"""
                                 #!/bin/sh
                                 if [ "$1" = "--version" ]; then echo 2.30.0; exit 0; fi
                                 printf '%s\n' "$@" > "{_fakeDir}/read-args.log"
                                 printf '%s' "$OP_SERVICE_ACCOUNT_TOKEN" > "{_fakeDir}/token.log"
                                 touch "{ReadMarker()}"
                                 {readBody}

                                 """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new CliSecretManager(CliSecretTool.OnePassword, path);
    }

    private CliSecretManager FakeOpPrintingKey()
    {
        var keyPath = Path.Combine(_fakeDir, "key.pem");
        File.WriteAllText(keyPath, new TestOpenSshKeyBuilder().Build());
        return FakeOp($"cat \"{keyPath}\"");
    }

    private async Task<RunResult> Run(string[] args, FakeConfigStore store, ICliSecretManager op, TextReader? stdin = null, string? envPassphrase = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();

        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(envPassphrase), new OnePasswordEncryptionKeyProvider(op)]));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var logger = new FakeLogger<EncryptionCommands>();
        _lastLogger = logger;
        var commands = new EncryptionCommands(bank, new NeverRunBws(), op, new StubEnvProvider(envPassphrase), new EncryptionSourceSidecar(BankPath()), logger);
        var exit = await TestData.CreateConfigCommands(store, encryptionCommands: commands)
            .RunAsync(parsed, new StandardStreams(stdin ?? TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return new RunResult(exit, stdout.ToString(), stderr.ToString(), bank);
    }

    private SqliteConnectionFactory BankFor(ICliSecretManager op, string? envPassphrase = null) =>
        new(Options(), new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
            [new StubEnvProvider(envPassphrase), new OnePasswordEncryptionKeyProvider(op)]));

    private static async Task<T> WithEnv<T>((string Name, string? Value)[] vars, Func<Task<T>> action)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken, vars);
        return await action();
    }

    private Task<RunResult> RunWithoutReferenceEnv(string[] args, FakeConfigStore store, ICliSecretManager op, TextReader? stdin = null, string? envPassphrase = null) =>
        WithEnv([(EncryptionCommands.OnePasswordReferenceEnvVar, null)], () => Run(args, store, op, stdin, envPassphrase));

    [RetryFact]
    public async Task OnePassword_OpMissing_ReturnsInstallErrorNamingOpAndChangesNothing()
    {
        var store = new FakeConfigStore();
        var missing = new CliSecretManager(CliSecretTool.OnePassword, Path.Combine(_fakeDir, "no-such-op"));

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, missing, new StringReader("\n"));

        exit.ShouldBe(ErrorCode.Key.SecretCliNotInstalled);
        err.ShouldContain("op not found");
        err.ShouldContain("1Password CLI");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [RetryFact]
    public async Task OnePassword_ReferenceViaStdin_PersistsAsSecretIdWithNoProjectId()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader($"{Reference}\n"));

        exit.ShouldBe(0, err);
        stdout.ShouldContain("encryption source set to 1password");
        err.ShouldContain("1Password");
        err.ShouldContain("PRAGMA rekey");
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("1password");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(Reference);
        store.Settings.ShouldNotContainKey(EncryptionSettingsKeys.ProjectId);
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.Source.ShouldBe("1password");
        sidecar.SecretId.ShouldBe(Reference);
        sidecar.ProjectId.ShouldBeNull();
    }

    [RetryFact]
    public async Task OnePassword_Fetch_RunsOpReadWithTheReferenceVerbatim()
    {
        var store = new FakeConfigStore();

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader($"{Reference}\n"));

        exit.ShouldBe(0, err);
        Logged("read-args.log").ShouldBe($"read\n{Reference}\n");
    }

    [RetryFact]
    public async Task OnePassword_InheritedServiceAccountToken_ReachesOpButNeverArgvOrStorage()
    {
        var store = new FakeConfigStore();

        var (exit, _, err, _) = await WithEnv([(EncryptionCommands.OnePasswordReferenceEnvVar, null), ("OP_SERVICE_ACCOUNT_TOKEN", "ops_inherited_tok")],
            () => Run(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader($"{Reference}\n")));

        exit.ShouldBe(0, err);
        Logged("token.log").ShouldBe("ops_inherited_tok");
        Logged("read-args.log").ShouldNotContain("ops_inherited_tok");
        store.Settings.Values.ShouldNotContain(v => v.Contains("ops_inherited_tok"));
        (await File.ReadAllTextAsync(SidecarPath(), TestContext.Current.CancellationToken)).ShouldNotContain("ops_inherited_tok");
    }

    [RetryFact]
    public async Task OnePassword_EmptyInput_OffersAnObviouslyFakePlaceholder()
    {
        var store = new FakeConfigStore();

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader("\n"));

        exit.ShouldBe(0, err);
        err.ShouldContain($"secret reference [{FakePlaceholder}]");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(FakePlaceholder);
    }

    [RetryFact]
    public async Task OnePassword_EnvOverrideConfigured_IsOfferedAsTheDefault()
    {
        var store = new FakeConfigStore();

        var (exit, _, err, _) = await WithEnv([(EncryptionCommands.OnePasswordReferenceEnvVar, "op://env-vault/env-item/key")],
            () => Run(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader("\n")));

        exit.ShouldBe(0, err);
        err.ShouldContain("secret reference [op://env-vault/env-item/key]");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe("op://env-vault/env-item/key");
    }

    [RetryFact]
    public async Task OnePassword_NonOpReference_IsRejectedBeforeAnyFetch()
    {
        var store = new FakeConfigStore();

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader("https://example.com/not-a-reference\n"));

        exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        err.ShouldContain("op://");
        File.Exists(ReadMarker()).ShouldBeFalse("op read must not run for a non-op:// reference");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [RetryFact]
    public async Task OnePassword_NonZeroExit_ReturnsFailedAndChangesNothing()
    {
        var store = new FakeConfigStore();
        var op = FakeOp("echo '[ERROR] item not found' >&2\nexit 1");

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, op, new StringReader($"{Reference}\n"));

        exit.ShouldBe(ErrorCode.Key.SecretCliFailed);
        err.ShouldContain("op failed (exit 1): [ERROR] item not found");
        err.ShouldNotContain("PRAGMA rekey");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 804 && r.Level == LogLevel.Debug && r.Message.Contains("op failed", StringComparison.Ordinal));
    }

    /// <summary>The 30 s bound is too long to wait out: a stub runner times out at once and records the bound the command asked for.</summary>
    [RetryFact]
    public async Task OnePassword_TimesOut_ExitsTimedOutChangesNothingAndAskedForThirtySeconds()
    {
        var store = new FakeConfigStore();
        var op = new TimingOutOp();

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, op, new StringReader($"{Reference}\n"));

        exit.ShouldBe(ErrorCode.Key.SecretCliTimedOut);
        err.ShouldContain("op timed out");
        op.FetchTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [RetryFact]
    public async Task OnePassword_NotAKey_Returns26WithTheSshFormatHint()
    {
        var store = new FakeConfigStore();
        var op = FakeOp("echo 'just a password'");

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, op, new StringReader($"{Reference}\n"));

        exit.ShouldBe(ErrorCode.Key.SecretNotAKey);
        err.ShouldContain("malformed OpenSSH private key");
        err.ShouldContain("?ssh-format=openssh");
        err.ShouldContain("SSH Key");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [RetryFact]
    public async Task OnePassword_EnvKeyedBank_RekeysAndReopensWithTheOnePasswordKey()
    {
        var store = new FakeConfigStore();
        var op = FakeOpPrintingKey();
        var bank = BankFor(op, "env-pass");
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, op, new StringReader($"{Reference}\n"), "env-pass");

        exit.ShouldBe(0, err);
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 801 && r.Message.Contains("1password", StringComparison.Ordinal));
        await using (await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        await Should.ThrowAsync<BankKeyMismatchException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken);
        });
    }

    [RetryFact]
    public async Task OnePassword_BankKeyedToSomethingElse_Returns21AndLeavesSidecarAndSettingsUntouched()
    {
        var store = new FakeConfigStore();
        var op = FakeOpPrintingKey();
        var bank = BankFor(op);
        await using (await bank.OpenBankWithKeyAsync("foreign-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, op, new StringReader($"{Reference}\n"));

        exit.ShouldBe(ErrorCode.Key.WrongKey);
        err.ShouldContain("encryption mismatch");
        err.ShouldContain("1password key");
        err.ShouldContain("'ai-raccoon encryption 1password'");
        File.Exists(SidecarPath()).ShouldBeFalse();
        store.Settings.ShouldBeEmpty();
        await using (await bank.OpenBankWithKeyAsync("foreign-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    [RetryFact]
    public async Task OnePassword_SwitchingFromBitwarden_ClearsTheProjectIdRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = "bw-project",
                [EncryptionSettingsKeys.SecretId] = "bw-secret"
            }
        };

        var (exit, _, err, _) = await RunWithoutReferenceEnv(["encryption", "1password"], store, FakeOpPrintingKey(), new StringReader($"{Reference}\n"));

        exit.ShouldBe(0, err);
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("1password");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(Reference);
        store.Settings.ShouldNotContainKey(EncryptionSettingsKeys.ProjectId);
    }

    [RetryFact]
    public async Task Show_OnePasswordRows_PrintsSourceAndReference()
    {
        var store = new FakeConfigStore
        {
            Settings = { [EncryptionSettingsKeys.Source] = "1password", [EncryptionSettingsKeys.SecretId] = Reference }
        };

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, FakeOpPrintingKey());

        exit.ShouldBe(0);
        stdout.ShouldContain("source: 1password");
        stdout.ShouldContain($"reference: {Reference}");
        stdout.ShouldNotContain("projectId");
    }

    [RetryFact]
    public async Task Show_NoRowsOnePasswordSidecar_PrintsSourceAndReference()
    {
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData("1password") { SecretId = "op://v/i/sidecar" });

        var (exit, stdout, _, _) = await Run(["encryption", "show"], new FakeConfigStore(), FakeOpPrintingKey());

        exit.ShouldBe(0);
        stdout.ShouldContain("source: 1password");
        stdout.ShouldContain("reference: op://v/i/sidecar");
    }

    /// <summary>The sidecar decides how the bank opens, so show reports it even when an interrupted switch left stale rows.</summary>
    [RetryFact]
    public async Task Show_SidecarDisagreesWithRows_ReportsTheSidecar()
    {
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData("1password") { SecretId = "op://v/i/sidecar" });
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden", [EncryptionSettingsKeys.ProjectId] = "stale-project",
                [EncryptionSettingsKeys.SecretId] = "stale-secret"
            }
        };

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, FakeOpPrintingKey());

        exit.ShouldBe(0);
        stdout.ShouldContain("source: 1password");
        stdout.ShouldContain("reference: op://v/i/sidecar");
        stdout.ShouldNotContain("stale");
    }

    [RetryFact]
    public async Task Unset_FromOnePassword_RekeysTheBankBackToEnv()
    {
        var store = new FakeConfigStore
        {
            Settings = { [EncryptionSettingsKeys.Source] = "1password", [EncryptionSettingsKeys.SecretId] = Reference }
        };
        var op = FakeOpPrintingKey();
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData("1password") { SecretId = Reference });
        var bank = BankFor(op);
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, stdout, err, _) = await Run(["encryption", "unset"], store, op, envPassphrase: "env-pass");

        exit.ShouldBe(0, err);
        stdout.Trim().ShouldBe("encryption source reset to env");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    [RetryFact]
    public async Task Unset_FromOnePasswordWithoutEnvPassphrase_WarnsNamingTheOnePasswordSource()
    {
        var store = new FakeConfigStore
        {
            Settings = { [EncryptionSettingsKeys.Source] = "1password", [EncryptionSettingsKeys.SecretId] = Reference }
        };
        var op = FakeOpPrintingKey();
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData("1password") { SecretId = Reference });
        var bank = BankFor(op);
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "unset"], store, op);

        exit.ShouldBe(ErrorCode.Key.NoEnvPassphrase);
        err.ShouldContain("stays keyed to the 1password secret");
        err.ShouldNotContain("bitwarden");
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 803 && r.Message.Contains("1password", StringComparison.Ordinal));
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("1password");
    }

    private sealed record RunResult(int Exit, string Out, string Err, SqliteConnectionFactory Bank);

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Passphrase(Source) { Value = passphrase });
    }

    /// <summary>The bws runner of an op test: any call is a defect.</summary>
    private sealed class NeverRunBws : ICliSecretManager
    {
        public Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the 1password flow must not run bws");
    }

    private sealed class TimingOutOp : ICliSecretManager
    {
        public TimeSpan? FetchTimeout { get; private set; }

        public Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (args is ["--version"])
            {
                return Task.FromResult(new CliSecretResult(0, "2.30.0", ""));
            }

            FetchTimeout = timeout;
            throw new CliSecretInvocationException(CliSecretFailure.TimedOut, $"op timed out after {(int)timeout.TotalSeconds}s");
        }
    }
}
