using System.Text.Json;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The wire contract every MCP client binds to: which tools exist, and each tool's parameter
///     names, JSON types and required-ness. Descriptions are deliberately excluded — editing a
///     description is not a breaking change, and a snapshot that fails on prose is a snapshot
///     nobody keeps honest.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpToolContractTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-contract-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>
    ///     One line per tool: name(param:type!, param:type?) — `!` required, `?` optional, in
    ///     declaration order. A renamed parameter, a changed type, or a tightened/loosened
    ///     required list all show up as a one-line diff.
    /// </summary>
    private const string ExpectedContract = """
                                            memory_delete(projectId:string!, hash:string!)
                                            memory_delete_context(projectId:string!, context:string!)
                                            memory_embed_pending(projectId:string!, limit:integer|null?)
                                            memory_ingest_directory(projectId:string!, path:string!, context:string|null?)
                                            memory_ingest_file(projectId:string!, path:string!, context:string|null?)
                                            memory_list(projectId:string!)
                                            memory_promotion_discard(projectId:string!, hash:string|null?)
                                            memory_promotion_list(projectId:string|null?, limit:integer?)
                                            memory_search(projectId:string!, query:string!, scope:string?, workspaceId:string|null?, limit:integer?, minScore:number?, rrfK:integer?, ftsWeight:integer?, vectorWeight:integer?, contextLabel:string|null?)
                                            memory_share(projectId:string!, hash:string!)
                                            memory_share_extract(projectIds:array!, mode:string?, limit:integer|null?, includeTtlRows:boolean?, autoPromote:boolean?, confirm:boolean?)
                                            memory_stats(projectId:string!)
                                            memory_sweep(projectId:string!, dryRun:boolean?)
                                            memory_sync(projectId:string!)
                                            memory_watch_add(projectId:string!, path:string!)
                                            memory_watch_remove(projectId:string!, path:string!)
                                            memory_watch_status(projectId:string!)
                                            memory_workspace_begin(projectId:string!, agentId:string|null?, name:string|null?)
                                            memory_workspace_consolidate(projectId:string!, workspaceId:string!, keep:array!)
                                            memory_workspace_discard(projectId:string!, workspaceId:string!)
                                            memory_workspace_status(projectId:string!, workspaceId:string!)
                                            memory_write(projectId:string!, content:string!, workspaceId:string|null?, agentId:string|null?, context:string|null?, sourceFile:string|null?, section:string|null?)
                                            """;

    [Fact]
    public void InputSchemas_MatchTheDeclaredContract()
    {
        var actual = string.Join('\n', Tools().Select(Describe));

        actual.ShouldBe(ExpectedContract);
    }

    /// <summary>
    ///     No tool opts into structured content, so the SDK declares no outputSchema and the
    ///     result travels as untyped text. That is the reason a Core-side envelope rename cannot
    ///     move a declared schema (#118 item 1) — and if a tool ever does opt in, this fails and
    ///     the envelope's shape becomes a published contract that needs its own snapshot.
    /// </summary>
    [Fact]
    public void NoTool_DeclaresAnOutputSchema()
    {
        var declaring = Tools()
            .Where(t => t.ProtocolTool.OutputSchema is not null)
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        declaring.ShouldBeEmpty(string.Join(", ", declaring));
    }

    private List<McpServerTool> Tools()
    {
        var host = McpServerSetup.CreateServerHost(
            new ServerConfig(7721, McpTransport.Stdio, TestData.CreateInfrastructureOptions(_dataRoot), default));
        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        return (options.ToolCollection ?? throw new InvalidOperationException("no tools registered"))
            .OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(McpServerTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        var required = schema.TryGetProperty("required", out var requiredNode)
            ? requiredNode.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];
        var parameters = schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject()
                .Select(p => $"{p.Name}:{TypeOf(p.Value)}{(required.Contains(p.Name) ? "!" : "?")}")
            : [];
        return $"{tool.ProtocolTool.Name}({string.Join(", ", parameters)})";
    }

    private static string TypeOf(JsonElement parameter)
    {
        if (!parameter.TryGetProperty("type", out var type))
        {
            return "<none>";
        }

        return type.ValueKind == JsonValueKind.Array
            ? string.Join('|', type.EnumerateArray().Select(e => e.GetString()))
            : type.GetString() ?? "<none>";
    }
}
