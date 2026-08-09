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
    public void RenderTrace_ListsEveryRegisteredActivitySource()
    {
        // docs/adr/0021: OtlpNames.Sources now also carries the ASP.NET Core request source, so the
        // rendered command must list it too — derived from the same list, not a second copy.
        MonitoringCommandRenderer.RenderTrace(12345).ShouldBe(
            $"dotnet-trace collect -p 12345 --providers {string.Join(',', OtlpNames.Sources)}");
    }

    [Fact]
    public void RenderPid_PrintsThePidAlone()
    {
        MonitoringCommandRenderer.RenderPid(12345).ShouldBe("12345");
    }
}
