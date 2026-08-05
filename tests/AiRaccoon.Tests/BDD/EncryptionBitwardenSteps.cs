using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
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

    /// <summary>
    ///     Resolved lazily: Reqnroll instantiates binding classes before BeforeScenario hooks run,
    ///     so an eager resolve would auto-construct a second context and break Hooks' registration.
    /// </summary>
    private EncryptionBitwardenFeatureContext? _ctx;

    private EncryptionBitwardenFeatureContext Ctx => _ctx ??= _scenarioContext.ScenarioContainer.Resolve<EncryptionBitwardenFeatureContext>();

    private EncryptionBitwardenFeatureContext.CliRun _lastCli = new(0, string.Empty, string.Empty);

    private string? _lastStartError;

    // ── Rule: The env variable remains the default key source ──

    [Given("^no encryption source configured$")]
    public void GivenNoEncryptionSource()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^AIRACCOON_DB_PASSPHRASE is set$")]
    public void GivenEnvPassphraseSet()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^the encryption source is bitwarden$")]
    public Task GivenEncryptionSourceBitwarden() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Given("^bws is installed$")]
    public void GivenBwsInstalled()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^bws is not installed$")]
    public void GivenBwsNotInstalled()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^a bank encrypted with the env passphrase$")]
    public Task GivenBankEncryptedWithEnvPassphrase() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Given("^an unencrypted ed25519 private key with seed <seed>$")]
    public void GivenEd25519KeyWithVectorSeed()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^an encrypted ed25519 private key$")]
    public void GivenEncryptedEd25519Key()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Given("^an RSA private key$")]
    public void GivenRsaPrivateKey()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    // ── When: CLI + server + provider ──

    [When("^the user runs encryption bitwarden$")]
    public Task WhenUserRunsEncryptionBitwarden() => RunCliAsync("\n\n", "encryption", "bitwarden");

    [When("^the user runs encryption bitwarden with project ([^ ]+) and secret ([^ ]+)$")]
    public Task WhenUserRunsEncryptionBitwardenWithIds(string projectId, string secretId) =>
        RunCliAsync($"{projectId}\n{secretId}\n", "encryption", "bitwarden");

    [When("^the user runs encryption bitwarden with -t <token>$")]
    public Task WhenUserRunsEncryptionBitwardenWithToken() =>
        RunCliAsync("\n\n", "encryption", "bitwarden", "-t", EncryptionBitwardenFeatureContext.KnownToken);

    [When("^the user runs encryption bitwarden with an unreachable secret id$")]
    public Task WhenUserRunsEncryptionBitwardenUnreachable() =>
        RunCliAsync($"{EncryptionBitwardenFeatureContext.ProjectId}\n{EncryptionBitwardenFeatureContext.UnreachableSecretId}\n",
            "encryption", "bitwarden");

    [When("^the user runs encryption show$")]
    public Task WhenUserRunsEncryptionShow() => RunCliAsync(string.Empty, "encryption", "show");

    [When("^the user runs encryption unset$")]
    public Task WhenUserRunsEncryptionUnset() => RunCliAsync(string.Empty, "encryption", "unset");

    [When("^the server opens the bank$")]
    public Task WhenServerOpensTheBank() => StartServerAsync();

    [When("^bws cannot reach Bitwarden$")]
    public void WhenBwsCannotReachBitwarden()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [When("^bws returns a different key than the bank was encrypted with$")]
    public void WhenBwsReturnsDifferentKey()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [When("^the source is switched to bitwarden and the bank is rekeyed to the derived raw key$")]
    public Task WhenSourceSwitchedAndBankRekeyed() => RunCliAsync("\n\n", "encryption", "bitwarden");

    [When("^the raw key is derived$")]
    public void WhenRawKeyDerived()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [When("^the provider reads the secret$")]
    public void WhenProviderReadsTheSecret()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    // ── Then ──

    [Then("^the bank opens with the env passphrase$")]
    public Task ThenBankOpensWithEnvPassphrase() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the command errors with a message to install bws and configure a token$")]
    public void ThenCommandErrorsWithInstallGuidance()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^no encryption source is changed$")]
    public Task ThenNoEncryptionSourceChanged() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the encryption source is bitwarden$")]
    public Task ThenEncryptionSourceIsBitwarden() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the project id and secret id are persisted$")]
    public Task ThenProjectAndSecretIdsPersisted() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the source is configured and validated with that token$")]
    public Task ThenSourceConfiguredAndValidatedWithToken() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the token is not stored anywhere$")]
    public Task ThenTokenNotStoredAnywhere() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the command errors with the bws failure message$")]
    public void ThenCommandErrorsWithBwsFailure()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^it equals SHA-256\\(\"([^\"]+)\" \\|\\| seed\\) formatted as x'<64hex>'$")]
    public void ThenDerivedKeyMatchesVector(string label)
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^it errors with a message that passphrase-protected keys are unsupported$")]
    public void ThenPassphraseProtectedRejected()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^it errors with a message that only ed25519 keys are supported$")]
    public void ThenRsaRejected()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^it exits with an actionable error$")]
    public void ThenServerExitsWithActionableError()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^no cached key is used$")]
    public Task ThenNoCachedKeyUsed() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the bank opens with the derived key$")]
    public Task ThenBankOpensWithDerivedKey() => throw new NotImplementedException("S5 RED — bindings to implement");

    [Then("^the server errors with an encryption mismatch$")]
    public void ThenServerErrorsWithEncryptionMismatch()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^the output warns that rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank$")]
    public void ThenOutputWarnsAboutRotationTrap()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^it prints bitwarden with the secret id$")]
    public void ThenShowPrintsBitwardenWithSecretId()
    {
        throw new NotImplementedException("S5 RED — bindings to implement");
    }

    [Then("^the encryption source is env$")]
    public Task ThenEncryptionSourceIsEnv() => throw new NotImplementedException("S5 RED — bindings to implement");

    // ── Shared machinery (implemented in the GREEN pass) ──

    private async Task RunCliAsync(string stdin, params string[] args)
    {
        _lastCli = await Ctx.RunCliAsync(stdin, args);
    }

    private async Task StartServerAsync()
    {
        _lastStartError = await Ctx.StartServerErrorAsync();
    }
}
