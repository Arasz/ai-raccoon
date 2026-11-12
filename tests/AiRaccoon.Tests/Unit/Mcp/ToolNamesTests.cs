using System.Reflection;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary><see cref="ToolNames" /> is the one hand-written list of tool names; it must equal the generated inventory.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolNamesTests
{
    [Fact]
    public void Constants_EqualTheGeneratedToolInventory()
    {
        var constants = typeof(ToolNames)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        constants.ShouldBe(McpToolInventory.Names().Order(StringComparer.Ordinal).ToList());
    }
}
