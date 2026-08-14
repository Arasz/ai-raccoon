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
///     encryption bitwarden/show/unset (docs/plans/encryption-bitwarden-implementation.md §S3): bws
///     presence with install guidance, interactive id collection, per-run-only token, reachability
///     validation, rotation warning, rekey→sidecar→settings persist order, and unset rekey-back/recovery.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigCommandsEncryptionTests : IDisposable
{
    // pinned vector: seed 00 01 … 1e 1f → x'72d2…' (TestOpenSshKeyBuilder builds that seed).
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    // The same seed under the pre-ADR-0012 construction SHA-256(Label ‖ seed) — what the migrate
    // verb has to find on disk and rekey away from.
    private const string LegacyDerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    // Obviously fake sidecar fixture ids, unrelated to the interactive-default behaviour below —
    // not real Bitwarden vault entries.
    private const string FixtureProjectId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string FixtureSecretId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    // The obviously-fake placeholders the fix offers instead, absent an env override.
    private const string FallbackProjectId = "00000000-0000-0000-0000-000000000000";
    private const string FallbackSecretId = "11111111-1111-1111-1111-111111111111";

    private const string BwsNotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    // Tests that set/clear AIRACCOON_DB_PASSPHRASE are serialized with CliCommandRunnerTests
    // via EnvScope, which takes TestData.EnvVarGate (the env var is process-global).
    private readonly string _dataRoot = TestData.CreateTempRoot();

    private FakeLogger<EncryptionCommands>? _lastLogger;

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private void WriteSidecar(string source, string projectId, string secretId) =>
        new EncryptionSourceSidecar(BankPath()).Write(new EncryptionData(source) { ProjectId = projectId, SecretId = secretId });

    private async Task<RunResult> Run(string[] args, FakeConfigStore store, FakeBwsRunner runner, TextReader? stdin = null, string? envPassphrase = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(envPassphrase), new BitwardenEncryptionKeyProvider(runner)]));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var logger = new FakeLogger<EncryptionCommands>();
        _lastLogger = logger;
        var encryptionState = new EncryptionSourceSidecar(BankPath());
        var envProvider = new StubEnvProvider(envPassphrase);
        var encryptionCommands = new EncryptionCommands(bank, runner, envProvider, encryptionState, logger);
        var exit = await TestData.CreateConfigCommands(store, encryptionCommands: encryptionCommands)
            .RunAsync(parsed, new StandardStreams(stdin ?? TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return new RunResult(exit, stdout.ToString(), stderr.ToString(), bank);
    }

    private static async Task<T> WithEnvPassphrase<T>(string? value, Func<Task<T>> action)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, value));
        return await action();
    }

    private static async Task<T> WithBitwardenIdEnvVars<T>(string? projectId, string? secretId, Func<Task<T>> action)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EncryptionCommands.ProjectIdEnvVar, projectId), (EncryptionCommands.SecretIdEnvVar, secretId));
        return await action();
    }


    /// <summary>
    ///     A Func&lt;T&gt; wrapper around an async body infers T = Task and releases the gate at the
    ///     first await, letting a second holder in while the override is still in force.
    /// </summary>
    [Fact]
    public async Task WithEnvPassphrase_HoldsTheGateUntilTheAwaitedBodyCompletes()
    {
        var bodyStarted = new TaskCompletionSource();
        var letBodyFinish = new TaskCompletionSource();

        var outer = WithEnvPassphrase("probe", async () =>
        {
            bodyStarted.SetResult();
            await letBodyFinish.Task;
            return 0;
        });

        // Entering the body means winning the process-global gate, which the rest of the suite also
        // wants; a short bound here measures contention, not the behaviour under test.
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

        // Bounded on purpose: the body is still running, so this acquire must lose.
        var stolen = await TestData.EnvVarGate.WaitAsync(TimeSpan.FromMilliseconds(500),
            TestContext.Current.CancellationToken);
        try
        {
            stolen.ShouldBeFalse("the gate was released while the protected body was still running");
        }
        finally
        {
            if (stolen)
            {
                TestData.EnvVarGate.Release();
            }

            letBodyFinish.SetResult();
            await outer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The seed is a byte[] local to the command; DeriveAndZeroSeed is the one call site free to clear it.</summary>
    [Fact]
    public void DeriveAndZeroSeed_DerivesTheRawKeyThenZeroesTheSeed()
    {
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var value = EncryptionCommands.DeriveAndZeroSeed(seed);

        value.ShouldBe(DerivedRawKey);
        seed.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public async Task Bitwarden_BwsMissing_ReturnsInstallErrorAndChangesNothing()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsInvocationException(BwsNotFoundText));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("bws not found");
        err.ShouldContain("https://bitwarden.com/help/cli/");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    /// <summary>
    ///     No env override configured: the interactive default must be an obviously fake
    ///     placeholder, never a baked-in id that identifies a real Bitwarden vault entry.
    /// </summary>
    [Fact]
    public async Task Bitwarden_InteractiveDefaults_NoEnvOverride_AreObviouslyFakePlaceholdersNotOwnerIds()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, err, _) = await WithBitwardenIdEnvVars(null, null, () =>
            Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n")));

        exit.ShouldBe(0);
        stdout.ShouldContain("encryption source set to bitwarden");
        err.ShouldContain("without PRAGMA rekey bricks the bank");
        err.ShouldContain($"project id [{FallbackProjectId}]");
        err.ShouldContain($"secret id [{FallbackSecretId}]");
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe("bitwarden");
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe(FallbackProjectId);
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(FallbackSecretId);
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe("bitwarden");
        sidecar.ProjectId.ShouldBe(FallbackProjectId);
        sidecar.SecretId.ShouldBe(FallbackSecretId);
        runner.Calls.Count.ShouldBe(2);
        runner.Calls[0].Args.ShouldBe(["--version"]);
        runner.Calls[0].Token.ShouldBeNull();
        runner.Calls[1].Args.ShouldBe(["secret", "get", FallbackSecretId]);
        runner.Calls[1].Token.ShouldBeNull();
    }

    /// <summary>Configured env ids are offered as the interactive default instead of the fallback placeholder.</summary>
    [Fact]
    public async Task Bitwarden_InteractiveDefaults_EnvOverrideConfigured_OffersTheConfiguredIds()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, _, err, _) = await WithBitwardenIdEnvVars("env-project-id", "env-secret-id", () =>
            Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n")));

        exit.ShouldBe(0);
        err.ShouldContain("project id [env-project-id]");
        err.ShouldContain("secret id [env-secret-id]");
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe("env-project-id");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe("env-secret-id");
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

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("bws failed (exit 1)");
        err.ShouldContain("secret not found (code: 404)");
        err.ShouldNotContain("PRAGMA rekey");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        // UX-F8: the default (non-quiet) console logs Information and above, so an Error/Warning
        // log here would double-print the same failure as "fail: ...EncryptionCommands[804] ..."
        // right after the clean "err" line above -- this event must sit below that threshold.
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 804 && r.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Bitwarden_MalformedSecretValue_ReturnsMalformedErrorAndNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, "not an openssh key", ""));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("malformed OpenSSH private key");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }


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
        _lastLogger!.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 801 && r.Level == LogLevel.Information
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
        // Unset crash-window fix (docs/plans/encryption-bitwarden-implementation.md, review
        // amendments): bank rekeyed back to env; sidecar is deleted to stay consistent.
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);
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
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await WithEnvPassphrase(null, () =>
            Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n")));

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("encryption mismatch");
        File.Exists(SidecarPath()).ShouldBeTrue();
        store.Settings.ShouldBeEmpty();
        await using (await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }


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
                [EncryptionSettingsKeys.ProjectId] = FixtureProjectId,
                [EncryptionSettingsKeys.SecretId] = FixtureSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: bitwarden");
        stdout.ShouldContain($"projectId: {FixtureProjectId}");
        stdout.ShouldContain($"secretId: {FixtureSecretId}");
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


    [Fact]
    public async Task Unset_NoBank_RemovesRowsAndSidecar()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = "bitwarden",
                [EncryptionSettingsKeys.ProjectId] = FixtureProjectId,
                [EncryptionSettingsKeys.SecretId] = FixtureSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);

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
                [EncryptionSettingsKeys.ProjectId] = FixtureProjectId,
                [EncryptionSettingsKeys.SecretId] = FixtureSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);
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
                [EncryptionSettingsKeys.ProjectId] = FixtureProjectId,
                [EncryptionSettingsKeys.SecretId] = FixtureSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(new EncryptionSourceSidecar(BankPath()),
                [new StubEnvProvider(null), new BitwardenEncryptionKeyProvider(runner)]));
        await using (await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "unset"], store, runner);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("stays keyed to the bitwarden secret");
        err.ShouldContain("set AIRACCOON_DB_PASSPHRASE and re-run");
        var logRecord = _lastLogger!.Collector.LatestRecord;
        logRecord.ShouldNotBeNull();
        logRecord.Id.Id.ShouldBe(803);
        // UX-F8: below the default console's Information threshold -- "err" above already
        // carries this message; a Warning/Error here would print it a second time as
        // "warn: ...EncryptionCommands[803] ...".
        logRecord.Level.ShouldBe(LogLevel.Debug);
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
                [EncryptionSettingsKeys.ProjectId] = FixtureProjectId,
                [EncryptionSettingsKeys.SecretId] = FixtureSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);
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


    [Fact]
    public void Constructor_NullBank_ThrowsArgumentNullException()
    {
        var ex = Should.Throw<ArgumentNullException>(() =>
            new EncryptionCommands(null!,
                new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")),
                new StubEnvProvider(null),
                new EncryptionSourceSidecar(BankPath()),
                new FakeLogger<EncryptionCommands>()));
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
                new FakeLogger<EncryptionCommands>()));
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
                new FakeLogger<EncryptionCommands>()));
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
                new FakeLogger<EncryptionCommands>()));
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

        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);

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

        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);

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

        var bankDirectory = Path.GetDirectoryName(BankPath());
        bankDirectory.ShouldNotBeNull();
        Directory.CreateDirectory(bankDirectory);
        await File.WriteAllBytesAsync(BankPath(), [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 % 251))],
            TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken);

        WriteSidecar("bitwarden", FixtureProjectId, FixtureSecretId);

        var (exit, _, err, _) = await Run(["encryption", "migrate"], store, runner);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("opens under neither");
        (await File.ReadAllBytesAsync(BankPath(), TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    private sealed record RunResult(int Exit, string Out, string Err, SqliteConnectionFactory Bank);

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Passphrase(Source) { Value = passphrase });
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

        public Task<BwsResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(([.. args], token));
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_result!);
        }
    }
}
