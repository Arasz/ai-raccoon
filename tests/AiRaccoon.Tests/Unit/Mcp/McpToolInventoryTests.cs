using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     <see cref="McpToolInventory" /> is the PRODUCT-side derived tool inventory: WP6's
///     MetricsReportService lives in Infrastructure, which cannot see the host's `AiRaccoon.Tools`
///     types, so the host resolves the inventory (the only place that can) and passes it in as data
///     (derive-or-delete-the-list; RegisteredTools.cs does the same reflection for tests, over the
///     same product assembly — this test cross-checks the two independent implementations agree).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpToolInventoryTests
{
    [Fact]
    public void Names_MatchesTheTestSideRegisteredToolsReflection()
    {
        McpToolInventory.Names().ShouldBe(RegisteredTools.Names());
    }

    [Fact]
    public void Names_ContainsMemoryPerformance()
    {
        McpToolInventory.Names().ShouldContain("memory_performance");
    }
}
