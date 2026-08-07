using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     encryption bitwarden/show/unset: bws presence with install guidance, interactive id
///     collection (owner defaults), per-run-only token, reachability validation, rotation
///     warning, rekey→sidecar→settings persist order, the amendment-1 env-key retry leg,
///     and the unset rekey-back/recovery paths (plan §S3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigCommandsEncryptionTests : IDisposable
{
    // §5.1 pinned vector: seed 00 01 … 1e 1f → x'72d2…' (TestOpenSshKeyBuilder builds that seed).
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    // The same seed under the pre-ADR-0012 construction SHA-256(Label ‖ seed) — what the migrate
    // verb has to find on disk and rekey away from.
    private const string LegacyDerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";
    private const string DefaultProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string DefaultSecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";

    private const string BwsNotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    // Tests that set/clear AIRACCOON_DB_PASSPHRASE are serialized with CliCommandRunnerTests
    // via TestData.EnvVarGate (the env var is process-global).
    private readonly string _dataRoot = TestData.CreateTempRoot();

    private FakeLogger? _lastLogger;

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private void WriteSidecar(string source, string projectId, string secretId) =>
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData(source) { ProjectId = projectId, SecretId = secretId });

    private async Task<RunResult> Run(string[] args, FakeConfigStore store, FakeBwsRunner runner, TextReader? stdin = null, string? envPassphrase = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(envPassphrase), new BitwardenEncryptionKeyProvider(runner)]));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var logger = new FakeLogger();
        _lastLogger = logger;
        var encryptionState = new EncryptionSourceSidecar(BankPath());
        var envProvider = new StubEnvProvider(envPassphrase);
        var encryptionCommands = new EncryptionCommands(bank, runner, envProvider, encryptionState, logger);
        var exit = await new ConfigCommands(encryptionCommands: encryptionCommands).RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr, stdin ?? TextReader.Null, cancellationToken: TestContext.Current.CancellationToken);
        return new RunResult(exit, stdout.ToString(), stderr.ToString(), bank);
    }

    private static T WithEnvPassphrase<T>(string? value, Func<T> action)
    {
        TestData.EnvVarGate.Wait();
        try
        {
            var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, value);
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }
    }

    // ── encryption bitwarden: presence, collection, token, validation ──

    [Fact]
    public async Task Bitwarden_BwsMissing_ReturnsInstallErrorAndChangesNothing()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsInvocationException(BwsNotFoundText));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("bws not found");
        err.ShouldContain("https://bitwarden.com/help/cli/");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task Bitwarden_InteractiveOwnerDefaults_PersistsSourceAndWarns()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(0);
        stdout.ShouldContain("encryption source set to bitwarden");
        err.ShouldContain("without PRAGMA rekey bricks the bank");
        err.ShouldContain($"project id [{DefaultProjectId}]");
        err.ShouldContain($"secret id [{DefaultSecretId}]");
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("bitwarden");
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe(DefaultProjectId);
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(DefaultSecretId);
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe("bitwarden");
        sidecar.ProjectId.ShouldBe(DefaultProjectId);
        sidecar.SecretId.ShouldBe(DefaultSecretId);
        runner.Calls.Count.ShouldBe(2);
        runner.Calls[0].Args.ShouldBe(["--version"]);
        runner.Calls[0].Token.ShouldBeNull();
        runner.Calls[1].Args.ShouldBe(["secret", "get", DefaultSecretId]);
        runner.Calls[1].Token.ShouldBeNull();
    }

    [Fact]
    public async Task Bitwarden_NonDefaultIdsViaStdin_ArePersisted()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("p-111\ns-222\n"));

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe("p-111");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe("s-222");
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.ProjectId.ShouldBe("p-111");
        sidecar.SecretId.ShouldBe("s-222");
    }

    [Fact]
    public async Task Bitwarden_Token_UsedForValidationOnlyNeverPersisted()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, _, _, _) = await Run(["encryption", "bitwarden", "-t", "tok-123"], store, runner,
            new StringReader("\n\n"));

        exit.ShouldBe(0);
        runner.Calls[0].Token.ShouldBeNull(); // the presence check never takes the token
        runner.Calls[1].Token.ShouldBe("tok-123");
        store.Settings.Values.ShouldNotContain(v => v.Contains("tok-123"));
        (await File.ReadAllTextAsync(SidecarPath(), TestContext.Current.CancellationToken)).ShouldNotContain("tok-123");
    }

    [Fact]
    public async Task Bitwarden_UnreachableSecret_ReturnsBwsErrorAndNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(1, "", "secret not found (code: 404)"));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("bws failed (exit 1)");
        err.ShouldContain("secret not found (code: 404)");
        err.ShouldNotContain("PRAGMA rekey");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task Bitwarden_MalformedSecretValue_ReturnsMalformedErrorAndNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, "not an openssh key", ""));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("malformed OpenSSH private key");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    // ── encryption bitwarden: bank rekey / self-heal / env-key retry leg ──

    [Fact]
    public async Task Bitwarden_EnvKeyedBank_RekeysToDerivedKey()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider("env-pass"), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"),
            "env-pass");

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("bitwarden");
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe("bitwarden");
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 2 && r.Level == LogLevel.Information
                                                                             && r.Message.Contains("Bank rekeyed to the bitwarden encryption key", StringComparison.Ordinal));
        // The bank now opens with the derived key (via the resolver: sidecar → bws fetch).
        await using (await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Bitwarden_SelfHeal_BankAlreadyDerivedKeyed_SkipsRekeyAndPersists()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider("env-pass"), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"),
            "env-pass");

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("bitwarden");
        File.Exists(SidecarPath()).ShouldBeTrue();
        await using (await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task Bitwarden_StaleSidecarAndEnvKeyedBank_ReportsEnvKeyedAndDeletesSidecar()
    {
        // Amendment 1 (unset crash window): bank rekeyed back to env, sidecar never deleted.
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner,
            new StringReader("\n\n"), "env-pass");

        exit.ShouldBe(0);
        err.ShouldContain("bank is env-keyed");
        err.ShouldContain("source was not switched");
        File.Exists(SidecarPath()).ShouldBeFalse();
        store.Settings.ShouldBeEmpty();
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task Bitwarden_StaleSidecarAndEnvKeyedBank_NoEnvPassphrase_ErrorsWithoutChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await WithEnvPassphrase(null, () =>
            Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n")));

        exit.ShouldBe(1);
        err.ShouldContain("encryption mismatch");
        File.Exists(SidecarPath()).ShouldBeTrue();
        store.Settings.ShouldBeEmpty();
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    // ── encryption show ──

    [Fact]
    public async Task Show_NoRowsNoSidecar_PrintsEnvSource()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("source: env");
    }

    [Fact]
    public async Task Show_BitwardenRows_PrintsSourceAndIds()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: bitwarden");
        stdout.ShouldContain($"projectId: {DefaultProjectId}");
        stdout.ShouldContain($"secretId: {DefaultSecretId}");
    }

    [Fact]
    public async Task Show_NoRows_SidecarFallback_PrintsBitwardenWithIds()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", "p-9", "s-9");

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: bitwarden");
        stdout.ShouldContain("projectId: p-9");
        stdout.ShouldContain("secretId: s-9");
    }

    // ── encryption unset ──

    [Fact]
    public async Task Unset_NoBank_RemovesRowsAndSidecar()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);

        var (exit, stdout, err, _) = await Run(["encryption", "unset"], store, runner);

        // No bank exists — nothing is stranded, so the cleanup runs regardless of the env passphrase.
        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("encryption source reset to env");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        err.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unset_RealBank_RekeysBackToEnvPassphrase()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, stdout, _, _) = await Run(["encryption", "unset"], store, runner, envPassphrase: "env-pass");

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("encryption source reset to env");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Unset_RealBank_NoEnvPassphrase_WarnsAndKeepsBankOnDerivedKey()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "unset"], store, runner);

        exit.ShouldBe(1);
        err.ShouldContain("stays keyed to the bitwarden secret");
        err.ShouldContain("set AIRACCOON_DB_PASSPHRASE and re-run");
        var logRecord = _lastLogger!.Collector.LatestRecord;
        logRecord.ShouldNotBeNull();
        logRecord.Id.Id.ShouldBe(4);
        logRecord.Level.ShouldBe(LogLevel.Warning);
        // The sidecar + rows stay (source remains bitwarden) so the documented retry works.
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("bitwarden");
        File.Exists(SidecarPath()).ShouldBeTrue();
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Unset_RealBank_RekeysBackFromResolverCreatedBank()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider("env-pass"), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "unset"], store, runner, envPassphrase: "env-pass");

        exit.ShouldBe(0, $"stderr: {err}");
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    // ── constructor null-guards ──

    [Fact]
    public void Constructor_NullBank_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(null!,
                new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")),
                new StubEnvProvider(null),
                new EncryptionSourceSidecar(BankPath()),
                new FakeLogger()));
        ex.ParamName.ShouldBe("bank");
    }

    [Fact]
    public void Constructor_NullBws_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(new SqliteConnectionFactory(Options(),
                    new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(null)])),
                null!,
                new StubEnvProvider(null),
                new EncryptionSourceSidecar(BankPath()),
                new FakeLogger()));
        ex.ParamName.ShouldBe("bws");
    }

    [Fact]
    public void Constructor_NullEnv_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(new SqliteConnectionFactory(Options(),
                    new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(null)])),
                new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")),
                null!,
                new EncryptionSourceSidecar(BankPath()),
                new FakeLogger()));
        ex.ParamName.ShouldBe("env");
    }

    [Fact]
    public void Constructor_NullSidecar_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(new SqliteConnectionFactory(Options(),
                    new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(null)])),
                new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")),
                new StubEnvProvider(null),
                null!,
                new FakeLogger()));
        ex.ParamName.ShouldBe("sidecar");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(new SqliteConnectionFactory(Options(),
                    new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(null)])),
                new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")),
                new StubEnvProvider(null),
                new EncryptionSourceSidecar(BankPath()),
                null!));
        ex.ParamName.ShouldBe("logger");
    }

    // ── encryption migrate: the ADR-0012 rekey verb ──

    /// <summary>The verb's gate: a legacy-keyed bank is rekeyed and its rows survive.</summary>
    [Fact]
    public async Task Migrate_LegacyKeyedBank_RekeysToTheCurrentDerivationAndKeepsData()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        // An installed user's bank: keyed with SHA-256(Label ‖ seed), holding a row.
        var legacyBank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(LegacyDerivedRawKey)]));
        await using (var connection = await legacyBank.OpenBankWithKeyAsync(LegacyDerivedRawKey, TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO t VALUES (1, 'survives')";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);

        var (exit, output, _, bank) = await Run(["encryption", "migrate"], store, runner);

        exit.ShouldBe(0);
        output.ShouldContain("rekeyed");

        await using var migrated = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken);
        await using var read = migrated.CreateCommand();
        read.CommandText = "SELECT value FROM t WHERE id = 1";
        (await read.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("survives");
    }

    [Fact]
    public async Task Migrate_BankAlreadyOnCurrentDerivation_ReportsNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var currentBank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()), [new StubEnvProvider(DerivedRawKey)]));
        await using (await currentBank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);

        var (exit, output, _, _) = await Run(["encryption", "migrate"], store, runner);

        exit.ShouldBe(0);
        output.ShouldContain("already");
    }

    /// <summary>A bank that opens under neither derivation is refused, and left byte-identical.</summary>
    [Fact]
    public async Task Migrate_CorruptBank_FailsLoudlyAndLeavesTheFileByteIdentical()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        Directory.CreateDirectory(Path.GetDirectoryName(BankPath())!);
        await File.WriteAllBytesAsync(BankPath(), [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 % 251))],
            TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken);

        WriteSidecar("bitwarden", DefaultProjectId, DefaultSecretId);

        var (exit, _, err, _) = await Run(["encryption", "migrate"], store, runner);

        exit.ShouldBe(1);
        err.ShouldContain("opens under neither");
        (await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    private sealed record RunResult(int Exit, string Out, string Err, SqliteConnectionFactory Bank);

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Passphrase GetPassphrase(EncryptionData encryptionData) => new(Source) { Value = passphrase };
    }

    private sealed class FakeBwsRunner : ICliSecretManager
    {
        private readonly BwsInvocationException? _exception;
        private readonly BwsResult? _result;

        public FakeBwsRunner(BwsResult result)
        {
            _result = result;
        }

        public FakeBwsRunner(BwsInvocationException exception)
        {
            _exception = exception;
        }

        public List<(IReadOnlyList<string> Args, string? Token)> Calls { get; } = [];

        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout)
        {
            Calls.Add(([.. args], token));
            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }
}
