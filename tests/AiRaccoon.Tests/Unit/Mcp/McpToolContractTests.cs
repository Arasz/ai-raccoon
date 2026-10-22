using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The wire contract every MCP client binds to: which tools exist, and each tool's parameter
///     names, JSON types and required-ness. Descriptions are excluded — editing one is not a
///     breaking change.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpToolContractTests : IAsyncLifetime
{
    /// <summary>
    ///     One line per tool: name(param:type!, param:type?) — `!` required, `?` optional, in
    ///     declaration order. A renamed parameter, a changed type, or a tightened/loosened
    ///     required list all show up as a one-line diff.
    /// </summary>
    // projectId is optional on every tool (cwd-default resolution). Snapshot token mapping:
    // `string?` = the CS1737 form — a NON-nullable `string projectId` carrying
    // [Optional][DefaultParameterValue("")] because a required parameter follows it; `string|null?`
    // = the nullable form (`string? projectId = null`), used where every following parameter is
    // already optional.
    private const string ExpectedContract = """
                                            add_memory(projectId:string?, content:string!, workspaceId:string|null?, agentId:string|null?, context:string|null?, sourceFile:string|null?, section:string|null?)
                                            add_watch(projectId:string?, path:string!)
                                            close_workspace(projectId:string?, workspaceId:string!, keep:array!)
                                            delete_context(projectId:string?, context:string!)
                                            delete_entry(projectId:string?, hash:string!)
                                            delete_memory_from_ranking(projectId:string|null?, hash:string|null?)
                                            discard_workspace(projectId:string?, workspaceId:string!)
                                            embed_deferred_entries(projectId:string|null?, limit:integer|null?)
                                            get_engine_statistics(projectId:string|null?, windowMinutes:integer|null?, bucketMinutes:integer|null?)
                                            get_entry(projectId:string?, hash:string!, entryType:string!)
                                            get_index(projectId:string|null?)
                                            get_memories_sharing_ranking(projectId:string|null?, limit:integer?, includeFullValue:boolean?, allProjects:boolean?)
                                            get_project_id(name:string!)
                                            get_quality_report(projectId:string?, from:string|null?)
                                            get_store_statistics(projectId:string|null?)
                                            get_watch_status(projectId:string|null?)
                                            get_workspace_status(projectId:string?, workspaceId:string!)
                                            ingest_directory(projectId:string?, path:string!, context:string|null?)
                                            ingest_file(projectId:string?, path:string!, context:string|null?)
                                            initialize_workspace(projectId:string|null?, agentId:string|null?, name:string|null?)
                                            query(projectId:string?, query:string!, sessionId:string!, scope:string?, workspaceId:string|null?, limit:integer?, minRelativeScore:number?, rrfK:integer|null?, ftsWeight:integer|null?, vectorWeight:integer|null?, sourceLambda:number|null?, consolidationThreshold:number|null?, docScoreFormula:string|null?, candidateWindow:string|null?, contextLabel:string|null?, entryType:string?, codeLimit:integer|null?, codeMinRelativeScore:number|null?)
                                            remove_watch(projectId:string?, path:string!)
                                            save_project_id_token(name:string|null?)
                                            set_memory_ttl(projectId:string?, hash:string!, ttlDays:integer|null?)
                                            set_memory_usage_record(projectId:string?, correlationId:string!, filePath:string!, servedRank:integer|null?)
                                            set_query_result_grade(projectId:string?, correlationId:string!, grade:integer!, note:string|null?)
                                            share_memory(projectId:string?, hash:string!)
                                            sync_to_cloud(projectId:string|null?)
                                            trigger_memory_cleanup(projectId:string|null?, dryRun:boolean?)
                                            trigger_memory_sharing(projectIds:array!, mode:string?, limit:integer|null?, includeTtlRows:boolean?, autoPromote:boolean?, confirm:boolean?)
                                            """;

    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-contract-tests");

    private IAsyncDisposable? _envGate;

    /// <summary>
    ///     Holds the env gate as a reader: this class opens a bank through the real host, so an
    ///     encryption test's window would make it open a plain bank with a key (docs/adr/0066).
    /// </summary>
    public async ValueTask InitializeAsync() =>
        _envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        if (_envGate is not null)
        {
            await _envGate.DisposeAsync();
        }
    }

    [Fact]
    public void InputSchemas_MatchTheDeclaredContract()
    {
        var actual = string.Join('\n', Tools().Select(Describe));

        actual.ShouldBe(ExpectedContract);
    }

    [Fact]
    public void MemorySearchEnumWireValues_AreTheDocumentedContract()
    {
        // The two enum knobs travel as plain string? params (the schema cannot carry value
        // lists for them), so the wire values are pinned here against the parse helpers —
        // a rename in SearchParameterSettingsKeys or a rejected value change is a contract
        // break, not an implementation detail (ADR-0083, plan S2).
        SearchParameterSettingsKeys.ParseDocScoreFormula("max").ShouldBe(Core.Memory.DocScoreFormula.Max);
        SearchParameterSettingsKeys.ParseDocScoreFormula("sum").ShouldBe(Core.Memory.DocScoreFormula.Sum);
        SearchParameterSettingsKeys.ParseDocScoreFormula("avg").ShouldBeNull();
        SearchParameterSettingsKeys.ParseCandidateWindow("max3x100").ShouldBe(CandidateWindowMode.Max3X100);
        SearchParameterSettingsKeys.ParseCandidateWindow("max5x50").ShouldBe(CandidateWindowMode.Max5X50);
        SearchParameterSettingsKeys.ParseCandidateWindow("max").ShouldBeNull();
    }

    /// <summary>
    ///     No tool opts into structured content, so the SDK declares no outputSchema and the result
    ///     travels as untyped text. If a tool ever opts in, this fails and the envelope's shape
    ///     becomes a published contract that needs its own snapshot.
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
        // Re-derived against the Http host (P2/ADR-0020): the ServerConfig transport is data-only
        // now — every value builds the same sole web host — so Http names the surviving shape.
        var host = McpServerSetup.CreateServerHost(
            new ServerConfig(0, McpTransport.Http, TestData.CreateInfrastructureOptions(_dataRoot)));
        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        return
        [
            .. (options.ToolCollection ?? throw new InvalidOperationException("no tools registered"))
            .OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal)
        ];
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
