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

    /// <summary>
    ///     F10 (owner ruling): "SDK permits - we don't, in this project tool name is required and
    ///     must be defined." The SDK allows an <c>[McpServerTool]</c> with no <c>Name</c> (falling
    ///     back to the method name), but this project does not: an explicit name is a project
    ///     requirement, so the ambiguous case must not be able to exist — this gate, not a
    ///     reconciliation of two readers that disagree about it, is what makes that true.
    /// </summary>
    [Fact]
    public void AllTools_DeclareAnExplicitName()
    {
        var unnamed = RegisteredTools.Methods()
            .Where(x => string.IsNullOrWhiteSpace(x.Attr.Name))
            .Select(x => $"{x.Class.Name}.{x.Method.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        unnamed.ShouldBeEmpty(
            $"every [McpServerTool] in AiRaccoon.Tools must declare an explicit Name: {string.Join(", ", unnamed)}");
    }
}
