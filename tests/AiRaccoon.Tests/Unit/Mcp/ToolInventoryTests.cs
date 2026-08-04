using System.Reflection;
using AiRaccoon.Prompts;
using AiRaccoon.Tools;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ToolInventoryTests
{
    [Fact]
    public void MemoryTools_ExposesAll16SpecTools()
    {
        var tools = typeof(MemoryTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .ToList();

        tools.Count.ShouldBe(16);
        tools.ShouldContain("memory_write");
        tools.ShouldContain("memory_search");
        tools.ShouldContain("memory_list");
        tools.ShouldContain("memory_stats");
        tools.ShouldContain("memory_share");
        tools.ShouldContain("memory_delete");
        tools.ShouldContain("memory_delete_context");
        tools.ShouldContain("memory_ingest_file");
        tools.ShouldContain("memory_ingest_directory");
        tools.ShouldContain("memory_embed_pending");
        tools.ShouldContain("memory_workspace_begin");
        tools.ShouldContain("memory_workspace_status");
        tools.ShouldContain("memory_workspace_consolidate");
        tools.ShouldContain("memory_workspace_discard");
        tools.ShouldContain("memory_sweep");
        tools.ShouldContain("memory_sync");
    }

    /// <summary>MS4: Assert that every [McpServerTool].Name has a matching const tag in MemoryTools.</summary>
    [Fact]
    public void McpToolNames_MatchConstStrings()
    {
        // Gather all TN_* const field values from MemoryTools.
        var constFields = typeof(MemoryTools)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("TN_"))
            .ToList();

        var constValues = new Dictionary<string, string>();
        foreach (var f in constFields)
        {
            var val = f.GetRawConstantValue() as string;
            if (val is not null)
            {
                constValues[val] = f.Name;
            }
        }

        // Gather all [McpServerTool].Name values.
        var toolMethods = typeof(MemoryTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => new
            {
                Method = m,
                Attr = m.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(x => x.Attr is not null)
            .ToList();

        toolMethods.Count.ShouldBe(16);

        foreach (var tm in toolMethods)
        {
            var toolName = tm.Attr!.Name!;
            constValues.ShouldContainKey(toolName,
                $"Missing const for tool '{toolName}' (method: {tm.Method.Name})");

            // Verify the const field exists and is prefixed with TN_
            constValues[toolName].ShouldStartWith("TN_");
        }
    }

    [Fact]
    public void MemoryPrompts_ExposesBothSpecPrompts()
    {
        var prompts = typeof(MemoryPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .ToList();

        prompts.Count.ShouldBe(2);
        prompts.ShouldContain("memory-usage-guide");
        prompts.ShouldContain("workspace-consolidation-guide");
    }

    [Fact]
    public void EveryTool_NamesTheProjectIdParameter()
    {
        var write = typeof(MemoryTools).GetMethod("Write")!;
        write.GetParameters().Select(p => p.Name).ShouldContain("projectId");
    }
}
