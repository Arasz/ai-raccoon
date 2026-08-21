using AiRaccoon.Core.Memory;
using AiRaccoon.Tests.TestHelpers;
using Reqnroll;

namespace AiRaccoon.Tests.BDD;

[Binding]
public sealed class Hooks(ScenarioContext scenarioContext)
{
    [BeforeScenario]
    public void BeforeScenario()
    {
        // The file-watcher feature's context extends the native-memory one; registering the
        // derived type covers both (NativeMemorySteps resolves MemoryFeatureContext).
        var ctx = new FileWatcherFeatureContext();
        scenarioContext.ScenarioContainer.RegisterInstanceAs(ctx);
        scenarioContext.ScenarioContainer.RegisterInstanceAs<MemoryFeatureContext>(ctx);
        scenarioContext.ScenarioContainer.RegisterInstanceAs<IMemoryStore>(ctx.Store);

        // The encryption-bitwarden feature's context (real resolver-backed bank + fake-bws
        // runner); registered under its own type — the native-memory registrations stay as-is.
        scenarioContext.ScenarioContainer.RegisterInstanceAs(new EncryptionBitwardenFeatureContext());

        // The code-corpus feature's context: its own SqliteMemoryStore composed with an
        // IIgnoreRulesProvider + IWatchStore wired into FileIngestor (ctx above does not carry
        // those), registered under its own type only.
        scenarioContext.ScenarioContainer.RegisterInstanceAs(new CodeCorpusFeatureContext());
    }

    /// <summary>
    ///     The bitwarden scenarios shell out to a fake bws script; the child inherits this
    ///     process's environment, so an ambient BWS_ACCESS_TOKEN on the host would reach the fake
    ///     and be rejected as an unknown token. The scenarios define their own world: the Bitwarden
    ///     vars are unset for the whole scenario, and only the token the runner sets explicitly
    ///     (the -t channel) ever reaches the fake.
    /// </summary>
    [BeforeScenario("bitwarden")]
    public async Task BeforeBitwardenScenario()
    {
        var scope = await EnvScope.AcquireAsync(CancellationToken.None,
            ("BWS_ACCESS_TOKEN", null), ("BW_ACCESS_TOKEN", null));
        scenarioContext.Set(scope);
    }

    [AfterScenario("bitwarden")]
    public async Task AfterBitwardenScenario()
    {
        if (scenarioContext.TryGetValue<EnvScope>(out var scope))
        {
            await scope.DisposeAsync();
        }
    }

    [AfterScenario]
    public void AfterScenario()
    {
        var ctx = scenarioContext.ScenarioContainer.Resolve<MemoryFeatureContext>();
        if (ctx is FileWatcherFeatureContext watchCtx)
        {
            watchCtx.StopWatchStack();
        }

        ctx.Dispose();
        scenarioContext.ScenarioContainer.Resolve<EncryptionBitwardenFeatureContext>().Dispose();
        scenarioContext.ScenarioContainer.Resolve<CodeCorpusFeatureContext>().Dispose();
    }
}
