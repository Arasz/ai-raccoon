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
    public void MemoryTools_ExposesAll17SpecTools()
    {
        var tools = typeof(MemoryTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .ToList();

        tools.Count.ShouldBe(17);
        tools.ShouldContain("memory_write");
        tools.ShouldContain("memory_search");
        tools.ShouldContain("memory_list");
        tools.ShouldContain("memory_stats");
        tools.ShouldContain("memory_share");
        tools.ShouldContain("memory_delete");
        tools.ShouldContain("memory_delete_context");
        tools.ShouldContain("memory_ingest_file");
        tools.ShouldContain("memory_ingest_directory");
        tools.ShouldContain("memory_configure");
        tools.ShouldContain("memory_embed_pending");
        tools.ShouldContain("memory_workspace_begin");
        tools.ShouldContain("memory_workspace_status");
        tools.ShouldContain("memory_workspace_consolidate");
        tools.ShouldContain("memory_workspace_discard");
        tools.ShouldContain("memory_sweep");
        tools.ShouldContain("memory_sync");
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
