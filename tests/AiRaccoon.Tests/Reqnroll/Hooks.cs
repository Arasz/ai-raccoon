using AiRaccoon.Core.Memory;
using Reqnroll;

namespace AiRaccoon.Tests.Reqnroll;

[Binding]
public sealed class Hooks
{
    private readonly ScenarioContext _scenarioContext;

    public Hooks(ScenarioContext scenarioContext)
    {
        _scenarioContext = scenarioContext;
    }

    [BeforeScenario]
    public void BeforeScenario()
    {
        var ctx = new MemoryFeatureContext();
        _scenarioContext.ScenarioContainer.RegisterInstanceAs(ctx);
        _scenarioContext.ScenarioContainer.RegisterInstanceAs<IMemoryStore>(ctx.Store);
    }

    [AfterScenario]
    public void AfterScenario()
    {
        var ctx = _scenarioContext.ScenarioContainer.Resolve<MemoryFeatureContext>();
        ctx.Dispose();
    }
}
