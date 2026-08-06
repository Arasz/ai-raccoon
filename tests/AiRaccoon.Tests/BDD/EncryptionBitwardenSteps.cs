using System.Data;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Dapper;
using Microsoft.Data.Sqlite;
using Reqnroll;
using Shouldly;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Bindings for docs/features/encryption-bitwarden/encryption-bitwarden.feature (15 scenarios).
///     CLI scenarios run the REAL CLI (CliArgs.Parse + ConfigCommands.RunAsync against the
///     scenario's resolver-backed store/bank with the fake-bws runner and a StringReader stdin —
///     the interactive prompts); "the server opens the bank" scenarios drive the real
///     factory/resolver eager-open path with Program.cs's loud-failure mapping; derivation and
///     rejection scenarios drive the real parser/derivation/provider against pinned §5.1 vectors.
/// </summary>
[Binding]
public sealed class EncryptionBitwardenSteps(ScenarioContext scenarioContext)
{
    private readonly ScenarioContext _scenarioContext = scenarioContext;

    private string? _capturedProjectId;

    private string? _capturedSecretId;

    /// <summary>
    ///     Resolved lazily: Reqnroll instantiates binding classes before BeforeScenario hooks run,
    ///     so an eager resolve would auto-construct a second context and break Hooks' registration.
    /// </summary>
    private EncryptionBitwardenFeatureContext? _ctx;

    private string? _derivedKey;

    private string? _fixtureSecretId;

    private EncryptionBitwardenFeatureContext.CliRun _lastCli = new(0, string.Empty, string.Empty);

    private Exception? _lastProviderError;

    private string? _lastStartError;

    private string? _pendingPem;

    private EncryptionBitwardenFeatureContext Ctx => _ctx ??= _scenarioContext.ScenarioContainer.Resolve<EncryptionBitwardenFeatureContext>();

    // ── Rule: The env variable remains the default key source ──

    [Given("^no encryption source configured$")]
    public void GivenNoEncryptionSource() =>
        // A fresh scenario has no sidecar — the env source is the implicit default.
        File.Exists(Ctx.SidecarPath).ShouldBeFalse();

    [Given("^AIRACCOON_DB_PASSPHRASE is set$")]
    public void GivenEnvPassphraseSet()
    {
        // The context's resolver env leg is a fixed stub passphrase (EnvPassphrase) — the
        // scenario's ambient environment is never read, so the suite stays deterministic.
    }

    [Given("^the encryption source is bitwarden$")]
    public Task GivenEncryptionSourceIsBitwarden() => Ctx.ConfigureBitwardenSourceAsync();

    [Given("^bws is installed$")]
    public void GivenBwsInstalled() => Ctx.InstallFakeBws();

    [Given("^bws is not installed$")]
    public void GivenBwsNotInstalled() =>
        // An absolute path that never exists → the runner surfaces the install guidance.
        Ctx.BwsExecutable = Path.Combine(Ctx.DataRoot, "fake-bws-missing", "bws");

    [Given("^a bank encrypted with the env passphrase$")]
    public Task GivenBankEncryptedWithEnvPassphrase() => Ctx.CreateEnvKeyedBankAsync();

    [Given("^an unencrypted ed25519 private key with seed <seed>$")]
    public void GivenEd25519KeyWithVectorSeed() =>
        // The literal "<seed>" is the §5.1 vector: the synthetic seed 00 01 … 1e 1f.
        _pendingPem = Ctx.BuildVectorPem();

    [Given("^an encrypted ed25519 private key$")]
    public void GivenEncryptedEd25519Key()
    {
        Ctx.InstallFakeBws();
        Ctx.WriteKeyFixture("key-bcrypt.pem", Ctx.BuildEncryptedPem());
        _fixtureSecretId = EncryptionBitwardenFeatureContext.BcryptSecretId;
    }

    [Given("^an RSA private key$")]
    public void GivenRsaPrivateKey()
    {
        Ctx.InstallFakeBws();
        Ctx.WriteKeyFixture("key-rsa.pem", Ctx.BuildRsaPem());
        _fixtureSecretId = EncryptionBitwardenFeatureContext.RsaSecretId;
    }

    // ── When: CLI + server + provider ──

    [When("^the user runs encryption bitwarden$")]
    public Task WhenUserRunsEncryptionBitwarden() => RunCliAsync("\n\n", "encryption", "bitwarden");

    [When("^the user runs encryption bitwarden with project ([^ ]+) and secret ([^ ]+)$")]
    public Task WhenUserRunsEncryptionBitwardenWithIds(string projectId, string secretId)
    {
        _capturedProjectId = projectId;
        _capturedSecretId = secretId;
        return RunCliAsync($"{projectId}\n{secretId}\n", "encryption", "bitwarden");
    }

    [When("^the user runs encryption bitwarden with -t <token>$")]
    public Task WhenUserRunsEncryptionBitwardenWithToken() => RunCliAsync("\n\n", "encryption", "bitwarden", "-t", EncryptionBitwardenFeatureContext.KnownToken);

    [When("^the user runs encryption bitwarden with an unreachable secret id$")]
    public Task WhenUserRunsEncryptionBitwardenUnreachable() =>
        RunCliAsync($"{EncryptionBitwardenFeatureContext.ProjectId}\n{EncryptionBitwardenFeatureContext.UnreachableSecretId}\n",
            "encryption", "bitwarden");

    [When("^the user runs encryption show$")]
    public Task WhenUserRunsEncryptionShow() => RunCliAsync(string.Empty, "encryption", "show");

    [When("^the user runs encryption unset$")]
    public Task WhenUserRunsEncryptionUnset() => RunCliAsync(string.Empty, "encryption", "unset");

    [When("^the server opens the bank$")]
    public async Task WhenServerOpensTheBank() => _lastStartError = await Ctx.StartServerErrorAsync();

    [When("^bws cannot reach Bitwarden$")]
    public void WhenBwsCannotReachBitwarden() => Ctx.WriteSidecar(EncryptionBitwardenFeatureContext.UnreachableSecretId);

    [When("^bws returns a different key than the bank was encrypted with$")]
    public void WhenBwsReturnsDifferentKey() => Ctx.WriteSidecar(EncryptionBitwardenFeatureContext.WrongKeySecretId);

    [When("^the source is switched to bitwarden and the bank is rekeyed to the derived raw key$")]
    public Task WhenSourceSwitchedAndBankRekeyed()
    {
        // This scenario's world has bws available (no "bws is not installed" Given): the config
        // action itself fetches the secret, so the fake executable must be present.
        Ctx.InstallFakeBws();
        return RunCliAsync("\n\n", "encryption", "bitwarden");
    }

    [When("^the raw key is derived$")]
    public void WhenRawKeyDerived() => _derivedKey = SshKeyDerivation.DeriveRawKey(OpenSshPrivateKeyParser.ParseSeed(_pendingPem!));

    [When("^the provider reads the secret$")]
    public void WhenProviderReadsTheSecret()
    {
        var provider = new BitwardenEncryptionKeyProvider(Ctx.NewRunner());
        try
        {
            provider.GetPassphrase(new EncryptionData("bitwarden") { SecretId = _fixtureSecretId });
        }
        catch (Exception ex)
        {
            _lastProviderError = ex;
        }
    }

    // ── Then ──

    [Then("^the bank opens with the env passphrase$")]
    public async Task ThenBankOpensWithEnvPassphrase()
    {
        _lastStartError.ShouldBeNull();
        // The resolver path (no sidecar → env source) opens the bank with the env passphrase…
        await using var reopened = await Ctx.Bank.OpenBankAsync();
        reopened.State.ShouldBe(ConnectionState.Open);
        // …and the bank is genuinely env-keyed: a keyless open is rejected.
        var wrong = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await Ctx.Bank.OpenBankWithKeyAsync(null);
        });
        wrong.SqliteErrorCode.ShouldBe(26);
    }

    [Then("^the command errors with a message to install bws and configure a token$")]
    public void ThenCommandErrorsWithInstallGuidance()
    {
        _lastCli.Exit.ShouldBe(1);
        _lastCli.Err.ShouldContain("bws not found");
        _lastCli.Err.ShouldContain("install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN");
    }

    [Then("^no encryption source is changed$")]
    public async Task ThenNoEncryptionSourceChanged()
    {
        _lastCli.Exit.ShouldBe(1);
        File.Exists(Ctx.SidecarPath).ShouldBeFalse();
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.Source)).ShouldBeNull();
    }

    [Then("^the encryption source is bitwarden$")]
    public async Task ThenEncryptionSourceIsBitwarden()
    {
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.Source))
            .ShouldBe("bitwarden");
        var sidecar = new EncryptionState(Ctx.BankPath).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe("bitwarden");
    }

    [Then("^the project id and secret id are persisted$")]
    public async Task ThenProjectAndSecretIdsPersisted()
    {
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.ProjectId)).ShouldBe(_capturedProjectId);
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.SecretId)).ShouldBe(_capturedSecretId);
        var sidecar = new EncryptionState(Ctx.BankPath).Read();
        sidecar.ShouldNotBeNull();
        sidecar.ProjectId.ShouldBe(_capturedProjectId);
        sidecar.SecretId.ShouldBe(_capturedSecretId);
    }

    [Then("^the source is configured and validated with that token$")]
    public async Task ThenSourceConfiguredAndValidatedWithToken()
    {
        _lastCli.Exit.ShouldBe(0);
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.Source))
            .ShouldBe("bitwarden");
        // The fake bws rejects any token other than the known one and logs every argv line —
        // the log proves the CLI passed -t <token> to the fetch (and only the fetch: the
        // presence check never takes the token).
        var calls = File.ReadAllText(Ctx.CallsLogPath);
        calls.ShouldContain($"secret get {EncryptionBitwardenFeatureContext.SecretId} -t {EncryptionBitwardenFeatureContext.KnownToken}");
        calls.ShouldNotContain($"--version -t");
    }

    [Then("^the token is not stored anywhere$")]
    public async Task ThenTokenNotStoredAnywhere()
    {
        var rows = await Ctx.ConfigStore.GetSettingsByPrefixAsync("encryption.");
        rows.Values.ShouldNotContain(v => v.Contains(EncryptionBitwardenFeatureContext.KnownToken));
        File.ReadAllText(Ctx.SidecarPath).ShouldNotContain(EncryptionBitwardenFeatureContext.KnownToken);
        _lastCli.Out.ShouldNotContain(EncryptionBitwardenFeatureContext.KnownToken);
        _lastCli.Err.ShouldNotContain(EncryptionBitwardenFeatureContext.KnownToken);
    }

    [Then("^the command errors with the bws failure message$")]
    public void ThenCommandErrorsWithBwsFailure()
    {
        _lastCli.Exit.ShouldBe(1);
        _lastCli.Err.ShouldContain("bws failed (exit 1)");
        _lastCli.Err.ShouldContain("connection refused");
    }

    [Then("^it equals SHA-256\\(\"([^\"]+)\" \\|\\| seed\\) formatted as x'<64hex>'$")]
    public void ThenDerivedKeyMatchesVector(string label)
    {
        label.ShouldBe(SshKeyDerivation.Label);
        _derivedKey.ShouldBe(EncryptionBitwardenFeatureContext.DerivedRawKey);
    }

    [Then("^it errors with a message that passphrase-protected keys are unsupported$")]
    public void ThenPassphraseProtectedRejected()
    {
        var ex = _lastProviderError.ShouldBeOfType<PassphraseProtectedKeyException>();
        ex.Message.ShouldContain("passphrase-protected keys are not supported");
    }

    [Then("^it errors with a message that only ed25519 keys are supported$")]
    public void ThenRsaRejected()
    {
        var ex = _lastProviderError.ShouldBeOfType<UnsupportedKeyTypeException>();
        ex.Message.ShouldContain("only ed25519 keys are supported");
    }

    [Then("^it exits with an actionable error$")]
    [Then("^the server exits with an actionable error$")]
    public async Task ThenServerExitsWithActionableError()
    {
        // These scenarios have no explicit "the server opens the bank" step — the start is the
        // consequence being asserted, so the Then drives it.
        await EnsureServerStartedAsync();
        // The committed Program.cs maps any resolve failure to one generic message (exit 1).
        _lastStartError!.ShouldBe("Failed to resolve encryption key");
    }

    [Then("^no cached key is used$")]
    public async Task ThenNoCachedKeyUsed()
    {
        // A second start fails identically — no key was ever cached from the failed fetch.
        _lastStartError!.ShouldBe("Failed to resolve encryption key");
        var again = await Ctx.StartServerErrorAsync();
        again.ShouldNotBeNull();
        again.ShouldBe("Failed to resolve encryption key");
    }

    [Then("^the bank opens with the derived key$")]
    public async Task ThenBankOpensWithDerivedKey()
    {
        // The resolver now resolves the bitwarden source: fake bws → derived key opens the bank.
        await using var reopened = await Ctx.Bank.OpenBankAsync();
        reopened.State.ShouldBe(ConnectionState.Open);
        // The env passphrase no longer opens it — the rekey landed.
        var wrong = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await Ctx.Bank.OpenBankWithKeyAsync(EncryptionBitwardenFeatureContext.EnvPassphrase);
        });
        wrong.SqliteErrorCode.ShouldBe(26);
    }

    [Then("^the server errors with an encryption mismatch$")]
    public async Task ThenServerErrorsWithEncryptionMismatch()
    {
        // No explicit "the server opens the bank" step in this scenario — the start is the
        // consequence being asserted, so the Then drives it.
        await EnsureServerStartedAsync();
        // Wrong derived key → the open fails; the committed Program.cs maps it to the generic open error.
        _lastStartError!.ShouldContain("Failed to open encrypted bank with bitwarden encryption source key");
    }

    [Then("^the output warns that rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank$")]
    public void ThenOutputWarnsAboutRotationTrap() => _lastCli.Err.ShouldContain("without PRAGMA rekey bricks the bank");

    [Then("^it prints bitwarden with the secret id$")]
    public void ThenShowPrintsBitwardenWithSecretId()
    {
        _lastCli.Exit.ShouldBe(0);
        _lastCli.Out.ShouldContain("source: bitwarden");
        _lastCli.Out.ShouldContain($"projectId: {EncryptionBitwardenFeatureContext.ProjectId}");
        _lastCli.Out.ShouldContain($"secretId: {EncryptionBitwardenFeatureContext.SecretId}");
    }

    [Then("^the encryption source is env$")]
    public async Task ThenEncryptionSourceIsEnv()
    {
        _lastCli.Exit.ShouldBe(0, $"cli stderr: {_lastCli.Err}");
        _lastCli.Out.ShouldContain("encryption source reset to env");
        File.Exists(Ctx.SidecarPath).ShouldBeFalse();
        // The settings mirror rows are gone too. After unset the bank stays keyed with whatever
        // the unset handler left it on (derived key without an ambient env passphrase, the
        // ambient passphrase when one was set and the rekey-back ran) — open with whichever
        // works and check the rows directly.
        var rows = await ReadEncryptionRowsAsync();
        rows.ShouldBeEmpty();
    }

    // ── Shared machinery ──

    private async Task RunCliAsync(string stdin, params string[] args) => _lastCli = await Ctx.RunCliAsync(stdin, args);

    /// <summary>Runs the eager-start open once per scenario; later Thens reuse the result.</summary>
    private async Task EnsureServerStartedAsync()
    {
        if (_lastStartError is null)
        {
            _lastStartError = await Ctx.StartServerErrorAsync();
        }
    }

    /// <summary>
    ///     Reads the settings rows under "encryption." from the bank, opening it with the key the
    ///     unset handler left the bank on: the derived key first (no env passphrase), then the
    ///     ambient AIRACCOON_DB_PASSPHRASE, then the context's own EnvPassphrase (the rekey-back
    ///     case, where the handler uses the injected env provider). Never reads the stub otherwise.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadEncryptionRowsAsync()
    {
        var candidates = new List<string?> { EncryptionBitwardenFeatureContext.DerivedRawKey };
        var ambient = new EnvEncryptionKeyProvider().GetPassphrase(new EncryptionData("env")).Value;
        if (!string.IsNullOrEmpty(ambient))
        {
            candidates.Add(ambient);
        }

        candidates.Add(EncryptionBitwardenFeatureContext.EnvPassphrase);

        foreach (var key in candidates)
        {
            try
            {
                await using var connection = await Ctx.Bank.OpenBankWithKeyAsync(key);
                var rows = await connection.QueryAsync<(string Key, string Value)>(
                    new CommandDefinition("SELECT key, value FROM settings WHERE key LIKE 'encryption.%'"));
                return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            }
            catch (SqliteException)
            {
                // Wrong candidate — try the next.
            }
        }

        throw new InvalidOperationException("the bank could not be opened with any expected post-unset key");
    }
}
