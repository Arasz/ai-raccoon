using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The MCP tool scope: which tools may run against the reserved <c>shared</c> document target.
///     The two sets below are the pin — the review record's F3 baseline with <c>memory_delete</c>
///     moved across (F12/F12a) — and the classification they are compared against is derived from
///     the tool surface, so a new tool cannot be added without stating whether it works there.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ScopeCheckTests
{
    /// <summary>Refused on the reserved target: the ordinary-only tools.</summary>
    private static readonly string[] OrdinaryOnly =
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

    /// <summary>Allowed on the reserved target: the reads, the ingestion, the watches, the telemetry.</summary>
    private static readonly string[] SharedAllowed =
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

    private readonly IScopeCheck _scope = new ScopeCheck(new ToolNameScope(), new SharedProjectScope());

    [Fact]
    public void EveryToolName_IsClassified()
    {
        var names = McpToolInventory.Names().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        string.Join(", ", RefusedOn("shared").Concat(AllowedOn("shared")).Order(StringComparer.Ordinal))
            .ShouldBe(string.Join(", ", names),
                "every tool in the surface is either refused on the reserved target or allowed on it");
    }

    [Fact]
    public void TheRefusedSet_IsExactlyTheOrdinaryOnlyTools() =>
        string.Join(", ", RefusedOn("shared")).ShouldBe(string.Join(", ", OrdinaryOnly),
            "the refused set is the ordinary-only tools, memory_delete among them");

    [Fact]
    public void TheAllowedSet_IsExactlyTheSharedAllowedTools() =>
        string.Join(", ", AllowedOn("shared")).ShouldBe(string.Join(", ", SharedAllowed),
            "the allowed set is the tools the reserved target supports: reads, ingestion, watches, telemetry");

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
    public void ANameThatIsNotATool_IsRefusedOnTheReservedTarget() =>
        _scope.Check("shared", "unknown").IsAllowed.ShouldBeFalse(
            "a name that is not a declared tool is refused on the reserved target: the map is the authority and the default is deny");

    private string[] RefusedOn(string projectId) =>
        [.. McpToolInventory.Names().Distinct(StringComparer.Ordinal)
            .Where(name => !_scope.Check(projectId, name).IsAllowed)
            .Order(StringComparer.Ordinal)];

    private string[] AllowedOn(string projectId) =>
        [.. McpToolInventory.Names().Distinct(StringComparer.Ordinal)
            .Where(name => _scope.Check(projectId, name).IsAllowed)
            .Order(StringComparer.Ordinal)];
}
