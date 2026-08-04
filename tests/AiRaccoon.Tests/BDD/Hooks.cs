using AiRaccoon.Core.Memory;
using Reqnroll;

namespace AiRaccoon.Tests.BDD;

[Binding]
public sealed class Hooks(ScenarioContext scenarioContext)
{
    [BeforeScenario]
    public void BeforeScenario()
    {
        var ctx = new MemoryFeatureContext();
        scenarioContext.ScenarioContainer.RegisterInstanceAs(ctx);
        scenarioContext.ScenarioContainer.RegisterInstanceAs<IMemoryStore>(ctx.Store);
    }

    [AfterScenario]
    public void AfterScenario()
    {
        var ctx = scenarioContext.ScenarioContainer.Resolve<MemoryFeatureContext>();
        ctx.Dispose();
    }
}
