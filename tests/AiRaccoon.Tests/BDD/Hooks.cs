using AiRaccoon.Core.Memory;
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
    }
}
