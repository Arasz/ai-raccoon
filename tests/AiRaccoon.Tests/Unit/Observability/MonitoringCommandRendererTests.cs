using AiRaccoon.Observability;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Golden shapes for the serve observability monitor-command renderer.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MonitoringCommandRendererTests
{
    [Fact]
    public void RenderCounters_IsTheBareMonitorCommand()
    {
        MonitoringCommandRenderer.RenderCounters(12345).ShouldBe("dotnet-counters monitor -p 12345");
    }

    [Fact]
    public void RenderTrace_ListsOnlyTheMemoryToolsActivitySource()
    {
        MonitoringCommandRenderer.RenderTrace(12345).ShouldBe(
            $"dotnet-trace collect -p 12345 --providers {string.Join(',', OtlpNames.Sources)}");
    }

    [Fact]
    public void RenderPid_PrintsThePidAlone()
    {
        MonitoringCommandRenderer.RenderPid(12345).ShouldBe("12345");
    }
}
