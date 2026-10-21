using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The MCP tool scope: the scope each tool declares, and which targets it may run against.
///     The two sets below are the pin — the review record's F3 baseline with `memory_delete`
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
        "code_get",
        "memory_delete",
        "memory_delete_context",
        "memory_promotion_discard",
        "memory_promotion_list",
        "memory_set_ttl",
        "memory_share",
        "memory_share_extract",
        "memory_sweep",
        "memory_workspace_begin",
        "memory_workspace_consolidate",
        "memory_workspace_discard",
        "memory_workspace_status",
        "memory_write"
    ];

    /// <summary>Any target: the tools the reserved target accepts — reads, ingestion, watches, telemetry.</summary>
    private static readonly string[] AllScope =
    [
        "memory_embed_pending",
        "memory_get",
        "memory_ingest_directory",
        "memory_ingest_file",
        "memory_list",
        "memory_performance",
        "memory_record_followthrough",
        "memory_record_grade",
        "memory_search",
        "memory_stats",
        "memory_sync",
        "memory_watch_add",
        "memory_watch_remove",
        "memory_watch_status",
        "project_id_get",
        "project_id_token_get"
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
            "the tools that need an ordinary project, memory_delete among them");

    [Fact]
    public void TheAllScopedTools_AreExactlyTheSharedAllowedTools() =>
        string.Join(", ", Declared(ToolScope.All)).ShouldBe(string.Join(", ", AllScope),
            "the tools the reserved target supports: reads, ingestion, watches, telemetry");

    [Fact]
    public void MemoryDelete_IsRefusedOnTheReservedTarget() =>
        _scope.Check("shared", "memory_delete").IsAllowed.ShouldBeFalse(
            "memory_delete is refused on the reserved target: its content is owned by the watch and the file");

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
