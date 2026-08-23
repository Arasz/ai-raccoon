using System.Reflection;
using System.Text.RegularExpressions;
using AiRaccoon.Prompts;
using AiRaccoon.Tools;
using ModelContextProtocol.Server;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ToolInventoryTests
{
    /// <summary>Derived from the product assembly — see TestHelpers/RegisteredTools.</summary>
    private static IEnumerable<(Type Class, MethodInfo Method, McpServerToolAttribute Attr)> ToolMethods() =>
        RegisteredTools.Methods();

    /// <summary>
    ///     Spec parity: every tool the spec names must exist. Deliberately no count assertion — the
    ///     count would be derived from the same reflection as the actual, and a comparison whose two
    ///     sides share a source can only ever pass. The count is guarded where it has an independent
    ///     second source: the packaged README, below.
    /// </summary>
    [Fact]
    public void ToolsNamespace_ExposesEverySpecTool()
    {
        var tools = ToolMethods()
            .Select(x => x.Attr.Name)
            .ToList();

        tools.ShouldContain("memory_write");
        tools.ShouldContain("memory_get");
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
        tools.ShouldContain("memory_performance");
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

    /// <summary>
    ///     Re-derived (ADR-0089 6b): a `projectId`/`projectIds` parameter is required exactly on a
    ///     tool whose body gates via <c>gate.RequireAsync</c> — <c>project_id_token_get</c> mints an
    ///     id and has none, so "every tool names it" is false on its own. Reads the body from source
    ///     the way <c>Unit.Layering.ToolMethodSizeTests</c> does, so this also catches the opposite
    ///     bug: a tool that names the parameter but never gates on it.
    /// </summary>
    [Fact]
    public void EveryTool_NamesTheProjectIdParameter()
    {
        var offenders = new List<string>();
        var gateCallsByMethodInClass = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);

        foreach (var (type, method, _) in ToolMethods())
        {
            var namesParameter = method.GetParameters()
                .Select(p => p.Name)
                .Any(n => n is "projectId" or "projectIds");

            if (!gateCallsByMethodInClass.TryGetValue(type.Name, out var gateCallsByMethod))
            {
                gateCallsByMethod = RequireAsyncCallsByMethod(type.Name);
                gateCallsByMethodInClass[type.Name] = gateCallsByMethod;
            }

            if (!gateCallsByMethod.TryGetValue(method.Name, out var gatesOnIt))
            {
                throw new InvalidOperationException($"Could not locate {type.Name}.{method.Name} in its source file.");
            }

            if (namesParameter != gatesOnIt)
            {
                offenders.Add($"{type.Name}.{method.Name} (namesProjectId={namesParameter}, callsRequireAsync={gatesOnIt})");
            }
        }

        offenders.ShouldBeEmpty(
            "a tool names projectId/projectIds exactly when its body gates via gate.RequireAsync: "
            + string.Join("; ", offenders));
    }

    /// <summary>Every [McpServerTool] method's name in a class's source file, mapped to whether its body calls gate.RequireAsync — one read and one parse per class file.</summary>
    private static Dictionary<string, bool> RequireAsyncCallsByMethod(string className)
    {
        var text = File.ReadAllText(RepoFile($"src/AiRaccoon/Tools/{className}.cs"));
        var attributeStarts = Regex.Matches(text, @"\[McpServerTool[^\]]*\]")
            .Select(m => m.Index)
            .Order()
            .ToList();

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var start in attributeStarts)
        {
            var end = attributeStarts.FirstOrDefault(i => i > start, text.Length);
            var region = text[start..end];
            var nameMatch = Regex.Match(region, @"public\s+(?:async\s+)?[\w<>?,\s\[\]]+\s+(\w+)\s*\(");
            if (nameMatch.Success)
            {
                result[nameMatch.Groups[1].Value] = region.Contains("gate.RequireAsync(", StringComparison.Ordinal);
            }
        }

        return result;
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
        // memory_* (10 memory tools et al.) plus code_* (WP6, the code corpus's own tool family
        // -- code_\w+, not a hardcoded code_get, so a second code tool doesn't need this regex
        // edited too; integration review small item 1) plus project_* (ADR-0089 6b, same reason).
        var documentedTools = Regex.Matches(readme, @"^\|\s*`(memory_\w+|code_\w+|project_\w+)`", RegexOptions.Multiline)
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
