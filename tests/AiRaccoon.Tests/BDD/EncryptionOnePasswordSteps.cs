using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Reqnroll;
using Shouldly;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Bindings for docs/features/encryption-1password/encryption-1password.feature: CLI scenarios drive
///     the real CLI end-to-end against a fake `op` script, and the server-start scenario drives the real
///     resolver and bank open.
/// </summary>
[Binding]
public sealed class EncryptionOnePasswordSteps(ScenarioContext scenarioContext)
{
    private const string Token = "ops_test_token_0123";

    private EncryptionOnePasswordFeatureContext.CliOutcome _lastCli = new(0, string.Empty, string.Empty);

    private string? _startError;

    /// <summary>Resolved lazily: Reqnroll builds binding classes before BeforeScenario hooks run.</summary>
    private EncryptionOnePasswordFeatureContext? _ctx;

    private EncryptionOnePasswordFeatureContext Ctx => _ctx ??= scenarioContext.ScenarioContainer.Resolve<EncryptionOnePasswordFeatureContext>();

    [Given("^op is installed$")]
    public void GivenOpInstalled() => Ctx.InstallFakeOp();

    [Given("^op is not installed$")]
    public void GivenOpNotInstalled() => Ctx.OpExecutable = Path.Combine(Ctx.DataRoot, "fake-op-missing", "op");

    [Given("^the encryption source is 1password$")]
    public Task GivenEncryptionSourceIsOnePassword() => Ctx.ConfigureOnePasswordSourceAsync();

    [Given("^the service account token (\\S+) is in the environment$")]
    public void GivenServiceAccountTokenInEnvironment(string token) =>
        // The 1password hook's EnvScope restores this variable when the scenario ends.
        Environment.SetEnvironmentVariable("OP_SERVICE_ACCOUNT_TOKEN", token);

    [When("^the user runs encryption 1password$")]
    public async Task WhenUserRunsEncryptionOnePassword() => _lastCli = await Ctx.RunCliAsync("\n", "encryption", "1password");

    [When("^the user runs encryption 1password with reference (.+)$")]
    public async Task WhenUserRunsEncryptionOnePasswordWithReference(string reference) =>
        _lastCli = await Ctx.RunCliAsync($"{reference}\n", "encryption", "1password");

    [When("^the user shows the encryption source$")]
    public async Task WhenUserShowsTheEncryptionSource() => _lastCli = await Ctx.RunCliAsync(string.Empty, "encryption", "show");

    [When("^the server opens the bank through op$")]
    public async Task WhenServerOpensTheBankThroughOp() => _startError = await Ctx.StartServerErrorAsync();

    [Then("^the command errors with a message to install op$")]
    public void ThenCommandErrorsWithInstallGuidance()
    {
        _lastCli.Exit.ShouldBe(ErrorCode.Key.SecretCliNotInstalled);
        _lastCli.Err.ShouldContain("op not found");
        _lastCli.Err.ShouldContain("install the 1Password CLI (op)");
    }

    [Then("^the 1password setup changed nothing$")]
    public async Task ThenNothingChanged()
    {
        _lastCli.Exit.ShouldNotBe(ErrorCode.Ok.Success);
        File.Exists(Ctx.SidecarPath).ShouldBeFalse();
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.Source)).ShouldBeNull();
    }

    [Then("^the encryption source is set to 1password$")]
    public async Task ThenSourceIsOnePassword()
    {
        _lastCli.Exit.ShouldBe(0, _lastCli.Err);
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.Source)).ShouldBe("1password");
        new EncryptionSourceSidecar(Ctx.BankPath).Read().Source.ShouldBe("1password");
    }

    [Then("^the reference is persisted as the secret id with no project id$")]
    public async Task ThenReferencePersisted()
    {
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.SecretId)).ShouldBe(EncryptionOnePasswordFeatureContext.Reference);
        (await Ctx.ConfigStore.GetSettingAsync(EncryptionSettingsKeys.ProjectId)).ShouldBeNull();
        var sidecar = new EncryptionSourceSidecar(Ctx.BankPath).Read();
        sidecar.SecretId.ShouldBe(EncryptionOnePasswordFeatureContext.Reference);
        sidecar.ProjectId.ShouldBeNull();
    }

    [Then("^op was asked to read the reference$")]
    public async Task ThenOpAskedToRead() =>
        (await File.ReadAllTextAsync(Ctx.CallsLogPath)).ShouldContain($"read {EncryptionOnePasswordFeatureContext.Reference} |");

    [Then("^op was not asked to read anything$")]
    public async Task ThenOpNotAskedToRead()
    {
        var calls = File.Exists(Ctx.CallsLogPath) ? await File.ReadAllTextAsync(Ctx.CallsLogPath) : string.Empty;
        calls.ShouldNotContain("read ");
    }

    [Then("^the command is refused as an invalid reference$")]
    public void ThenRefusedAsInvalidReference()
    {
        _lastCli.Exit.ShouldBe(ErrorCode.Usage.InvalidValue);
        _lastCli.Err.ShouldContain("op://");
    }

    [Then("^the bank opens with the key op returned$")]
    public async Task ThenBankOpensWithOpKey()
    {
        _startError.ShouldBeNull();
        await using var opened = await Ctx.OnePasswordBank.OpenBankWithKeyAsync(EncryptionOnePasswordFeatureContext.DerivedRawKey);
    }

    [Then("^op received the token through its environment and not its arguments$")]
    public async Task ThenTokenViaEnvironmentOnly()
    {
        var calls = await File.ReadAllTextAsync(Ctx.CallsLogPath);
        calls.ShouldContain($"read {EncryptionOnePasswordFeatureContext.Reference} | env:OP_SERVICE_ACCOUNT_TOKEN={Token}");
        calls.ShouldNotContain($"-t {Token}");
        calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(" | ")[0]).ShouldAllBe(argv => !argv.Contains(Token));
    }

    [Then("^it prints 1password with the reference$")]
    public void ThenShowPrintsOnePassword()
    {
        _lastCli.Exit.ShouldBe(0);
        _lastCli.Out.ShouldContain("source: 1password");
        _lastCli.Out.ShouldContain($"reference: {EncryptionOnePasswordFeatureContext.Reference}");
    }
}
