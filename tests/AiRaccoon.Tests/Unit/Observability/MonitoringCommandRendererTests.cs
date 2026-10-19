using AiRaccoon.Observability;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Contract test: RenderCounters must name every meter from OtlpNames.Meters and only those.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MonitoringCommandRendererTests
{
    [Fact]
    public void RenderCounters_NamesEveryMeterAndOnlyMeters_FromOtlpNames()
    {
        var result = MonitoringCommandRenderer.RenderCounters(12345);

        result.ShouldStartWith("dotnet-counters monitor -p 12345");
        result.ShouldContain("--counters ");

        // The value is the meter registry itself, not a second copy of it: every meter the
        // server registers is named, and nothing else is.
        var countersValue = result.Split("--counters ")[1];
        countersValue.ShouldBe(string.Join(',', OtlpNames.Meters));
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
