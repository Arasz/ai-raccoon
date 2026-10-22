using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The MCP tool scope: the scope each tool declares, and which targets it may run against.
///     The two sets below are the pin — the review record's F3 baseline with `delete_entry`
///     moved across (F12/F12a) — and the classification they are compared against is derived
///     from the tool surface, so a new tool cannot be added without stating its scope.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ScopeCheckTests
{
    /// <summary>Ordinary projects only: the tools the reserved target refuses.</summary>
    private static readonly string[] ProjectScope =
    [
        "add_memory",
        "close_workspace",
        "delete_context",
        "delete_entry",
        "delete_memory_from_ranking",
        "discard_workspace",
        "get_memories_sharing_ranking",
        "get_workspace_status",
        "initialize_workspace",
        "set_memory_ttl",
        "share_memory",
        "trigger_memory_cleanup",
        "trigger_memory_sharing",
    ];

    /// <summary>Any target: the tools the reserved target accepts — reads, ingestion, watches, telemetry.</summary>
    private static readonly string[] AllScope =
    [
        "add_watch",
        "embed_deferred_entries",
        "get_engine_statistics",
        "get_entry",
        "get_index",
        "get_project_id",
        "get_quality_report",
        "get_store_statistics",
        "get_watch_status",
        "ingest_directory",
        "ingest_file",
        "query",
        "remove_watch",
        "save_project_id_token",
        "set_memory_usage_record",
        "set_query_result_grade",
        "sync_to_cloud",
    ];

    private readonly IToolNameScope _names = new ToolNameScope();
    private readonly IScopeCheck _scope = new ScopeCheck(new ToolNameScope(), new SharedProjectScope());

    [Fact]
    public void EveryToolName_IsClassified()
    {
        var names = McpToolInventory.Names().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        string.Join(", ", Classified().Order(StringComparer.Ordinal))
            .ShouldBe(string.Join(", ", names),
                "every tool in the surface is either refused on the reserved target or allowed on it");
    }

    [Fact]
    public void TheProjectScopedTools_AreExactlyTheOrdinaryOnlyTools() =>
        string.Join(", ", Declared(ToolScope.Project)).ShouldBe(string.Join(", ", ProjectScope),
            "the tools that need an ordinary project, delete_entry among them");

    [Fact]
    public void TheAllScopedTools_AreExactlyTheSharedAllowedTools() =>
        string.Join(", ", Declared(ToolScope.All)).ShouldBe(string.Join(", ", AllScope),
            "the tools the reserved target supports: reads, ingestion, watches, telemetry");

    [Fact]
    public void MemoryDelete_IsRefusedOnTheReservedTarget() =>
        _scope.Check("shared", "delete_entry").IsAllowed.ShouldBeFalse(
            "delete_entry is refused on the reserved target: its content is owned by the watch and the file");

    [Fact]
    public void AnOrdinaryProject_AllowsEveryTool()
    {
        foreach (var name in McpToolInventory.Names().Distinct(StringComparer.Ordinal))
        {
            _scope.Check("acme", name).IsAllowed.ShouldBeTrue($"an ordinary project runs {name}");
        }
    }

    [Fact]
    public void ANameThatIsNotATool_IsRefusedOnTheReservedTarget()
    {
        _names.ScopeOf("unknown").ShouldBe(ToolScope.Project,
            "a name that is not a declared tool is ordinary-project scoped: the map is the authority and the default is deny");
        _scope.Check("shared", "unknown").IsAllowed.ShouldBeFalse(
            "a name that is not a declared tool is refused on the reserved target");
    }

    [Fact]
    public void ASharedOnlyTool_IsRefusedOnAnOrdinaryProject()
    {
        var scope = new ScopeCheck(new FixedScope("shared_only_tool", ToolScope.Shared), new SharedProjectScope());

        scope.Check("shared", "shared_only_tool").IsAllowed.ShouldBeTrue(
            "a tool declared ToolScope.Shared runs against the reserved shared target");
        scope.Check("acme", "shared_only_tool").IsAllowed.ShouldBeFalse(
            "a tool declared ToolScope.Shared is refused on an ordinary project");
    }

    private IEnumerable<string> Declared(ToolScope scope) =>
        [.. McpToolInventory.Names().Distinct(StringComparer.Ordinal)
            .Where(name => _names.ScopeOf(name) == scope)
            .Order(StringComparer.Ordinal)];

    private IEnumerable<string> Classified() =>
        [.. McpToolInventory.Names().Distinct(StringComparer.Ordinal)
            .Where(name => _scope.Check("shared", name).IsAllowed
                           || _scope.Check("acme", name).IsAllowed)
            .Order(StringComparer.Ordinal)];

    /// <summary>A scope map that gives one tool a fixed scope, for the rule's third value.</summary>
    private sealed class FixedScope(string toolName, ToolScope scope) : IToolNameScope
    {
        public ToolScope ScopeOf(string name) => name == toolName ? scope : ToolScope.All;
    }
}
