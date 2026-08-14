using System.Reflection;
using System.Text.RegularExpressions;
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
    /// <summary>Every [McpServerTool] method in the Tools namespace — the split safety net.</summary>
    private static IEnumerable<(Type Class, MethodInfo Method, McpServerToolAttribute Attr)> ToolMethods()
    {
        foreach (var type in typeof(MemoryTools).Assembly.GetTypes()
                     .Where(t => t.Namespace == "AiRaccoon.Tools" && t.IsClass && !t.IsAbstract))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (type, method, attr);
                }
            }
        }
    }

    [Fact]
    public void ToolsNamespace_ExposesAll24SpecTools()
    {
        var tools = ToolMethods()
            .Select(x => x.Attr.Name)
            .ToList();

        tools.Count.ShouldBe(25);
        tools.ShouldContain("memory_write");
        tools.ShouldContain("memory_search");
        tools.ShouldContain("memory_list");
        tools.ShouldContain("memory_stats");
        tools.ShouldContain("memory_share");
        tools.ShouldContain("memory_share_extract");
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
        tools.ShouldContain("memory_set_ttl");
        tools.ShouldContain("memory_sync");
        tools.ShouldContain("memory_promotion_list");
        tools.ShouldContain("memory_promotion_discard");
        tools.ShouldContain("memory_watch_add");
        tools.ShouldContain("memory_watch_status");
        tools.ShouldContain("memory_watch_remove");
        tools.ShouldContain("memory_record_followthrough");
        tools.ShouldContain("memory_record_grade");
    }

    /// <summary>Assert that every [McpServerTool].Name has a matching Tn const in its own class.</summary>
    [Fact]
    public void McpToolNames_MatchConstStrings()
    {
        foreach (var group in ToolMethods().GroupBy(x => x.Class))
        {
            var constValues = new Dictionary<string, string>();
            foreach (var field in group.Key.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.Name.StartsWith("Tn")))
            {
                if (field.GetRawConstantValue() is string val)
                {
                    constValues[val] = field.Name;
                }
            }

            foreach (var (_, method, attr) in group)
            {
                var toolName = attr.Name!;
                constValues.ShouldContainKey(toolName,
                    $"Missing const for tool '{toolName}' (class: {group.Key.Name}, method: {method.Name})");
                constValues[toolName].ShouldStartWith("Tn");
            }
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
        var missing = ToolMethods()
            .Where(x => !x.Method.GetParameters()
                .Select(p => p.Name)
                .Any(n => n is "projectId" or "projectIds"))
            .Select(x => $"{x.Class.Name}.{x.Method.Name}")
            .ToList();

        missing.ShouldBeEmpty();
    }

    /// <summary>The server reference doc lists all MCP tools — its tool count heading must track the registry, not go stale.</summary>
    [Fact]
    public void PackagedReadme_ToolsHeading_MatchesActualToolCount()
    {
        var readme = File.ReadAllText(RepoFile("docs/reference/agent-memory-server.md"));
        var match = Regex.Match(readme, @"^## Tools \((\d+)\)", RegexOptions.Multiline);

        match.Success.ShouldBeTrue("Could not find the '## Tools (N)' heading in docs/reference/agent-memory-server.md.");
        int.Parse(match.Groups[1].Value).ShouldBe(ToolMethods().Count());
    }

    /// <summary>The doc's tools table must list exactly the registered tools — not just the right count.</summary>
    [Fact]
    public void PackagedReadme_ToolsTable_ListsExactlyTheRegisteredTools()
    {
        var readme = File.ReadAllText(RepoFile("docs/reference/agent-memory-server.md"));
        var documentedTools = Regex.Matches(readme, @"^\|\s*`(memory_\w+)`", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var registeredTools = ToolMethods()
            .Select(x => x.Attr.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        documentedTools.ShouldBe(registeredTools);
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {relative} from the test output directory.");
    }
}
