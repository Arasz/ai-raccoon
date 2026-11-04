using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
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
    // projectId is optional on every memory tool (cwd-default resolution); the decision tools
    // require it (`string!`) because they never infer a project. Snapshot token mapping:
    // `string?` = the CS1737 form — a NON-nullable `string projectId` carrying
    // [Optional][DefaultParameterValue("")] because a required parameter follows it; `string|null?`
    // = the nullable form (`string? projectId = null`), used where every following parameter is
    // already optional.
    private const string ExpectedContract = """
                                            add_memory(projectId:string?, content:string!, workspaceId:string|null?, agentId:string|null?, context:string|null?, sourceFile:string|null?, section:string|null?)
                                            add_watch(projectId:string?, path:string!)
                                            amend_decision(projectId:string!, decisionId:string!, taskId:string!, sessionId:string!, requestId:string!, expectedVersion:integer!, changeReason:string!, content:object!, actor:string|null?)
                                            close_workspace(projectId:string?, workspaceId:string!, keep:array!)
                                            delete_context(projectId:string?, context:string!)
                                            delete_entry(projectId:string?, hash:string!)
                                            delete_memory_from_ranking(projectId:string|null?, hash:string|null?)
                                            discard_workspace(projectId:string?, workspaceId:string!)
                                            embed_deferred_entries(projectId:string|null?, limit:integer|null?)
                                            export_decisions(projectId:string!, selector:object|null?)
                                            find_decision_precedents(projectId:string!, scenario:string!, category:string|null?, limit:integer?, includeInactive:boolean?)
                                            get_decision(projectId:string!, decisionId:string!, version:integer|null?, includeHistory:boolean?)
                                            get_decision_graph(projectId:string!, decisionId:string!, direction:string?, depth:integer?, maxNodes:integer?, includeContext:boolean?)
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
                                            link_decisions(projectId:string!, taskId:string!, sessionId:string!, requestId:string!, sourceId:string!, targetId:string!, kind:string!, reason:string!, expectedProjectRevision:integer!, actor:string|null?)
                                            preview_decisions(projectId:string!, selector:object|null?, maxDecisions:integer?, maxEdges:integer?, maxChars:integer?)
                                            query(projectId:string?, query:string!, sessionId:string!, scope:string?, workspaceId:string|null?, limit:integer?, minRelativeScore:number?, rrfK:integer|null?, ftsWeight:integer|null?, vectorWeight:integer|null?, sourceLambda:number|null?, consolidationThreshold:number|null?, docScoreFormula:string|null?, candidateWindow:string|null?, contextLabel:string|null?, entryType:string?, codeLimit:integer|null?, codeMinRelativeScore:number|null?)
                                            query_decisions(projectId:string!, query:string|null?, category:string|null?, statuses:array|null?, selector:object|null?, limit:integer?, afterId:string|null?)
                                            record_decision(projectId:string!, taskId:string!, sessionId:string!, requestId:string!, title:string!, category:string!, scenario:string!, outcome:string!, reasoning:string!, status:string?, confidence:number|null?, maker:string|null?, alternatives:array|null?, consequences:array|null?, sources:array|null?, validFrom:string|null?, validUntil:string|null?, actor:string|null?)
                                            register_decision_task(projectId:string!, taskId:string!, sessionId:string!, requestId:string!, title:string!, sourceRef:string|null?, actor:string|null?)
                                            remove_watch(projectId:string?, path:string!)
                                            save_project_id_token(name:string|null?)
                                            set_memory_ttl(projectId:string?, hash:string!, ttlDays:integer|null?)
                                            set_memory_usage_record(projectId:string?, correlationId:string!, filePath:string!, servedRank:integer|null?)
                                            set_query_result_grade(projectId:string?, correlationId:string!, grade:integer!, note:string|null?)
                                            share_memory(projectId:string?, hash:string!)
                                            supersede_decision(projectId:string!, taskId:string!, sessionId:string!, requestId:string!, replacementId:string!, replacedId:string!, expectedReplacementVersion:integer!, expectedReplacedVersion:integer!, reason:string!, actor:string|null?)
                                            sync_to_cloud(projectId:string|null?)
                                            transition_decision(projectId:string!, decisionId:string!, taskId:string!, sessionId:string!, requestId:string!, expectedVersion:integer!, targetStatus:string!, changeReason:string!, actor:string|null?)
                                            trigger_memory_cleanup(projectId:string|null?, dryRun:boolean?)
                                            trigger_memory_sharing(projectIds:array!, mode:string?, limit:integer|null?, includeTtlRows:boolean?, autoPromote:boolean?, confirm:boolean?)
                                            unlink_decisions(projectId:string!, edgeId:string!, taskId:string!, sessionId:string!, requestId:string!, reason:string!, expectedProjectRevision:integer!, actor:string|null?)
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
    ///     Only the decision tools opt into structured content, so only they declare an
    ///     outputSchema; the memory tools keep their untyped text envelope. Each decision schema
    ///     publishes the envelope shape: data carrying projectId, projectRevision, value and replayed.
    /// </summary>
    [Fact]
    public void OnlyTheDecisionTools_DeclareAnOutputSchema()
    {
        var declaring = Tools().Where(t => t.ProtocolTool.OutputSchema is not null).ToList();

        declaring.Select(t => t.ProtocolTool.Name).ShouldBe(
            DecisionOperations.All.Select(o => o.Tool).Order(StringComparer.Ordinal));
        foreach (var tool in declaring)
        {
            var data = tool.ProtocolTool.OutputSchema!.Value.GetProperty("properties").GetProperty("data").GetProperty("properties");
            foreach (var field in new[] { "projectId", "projectRevision", "value", "replayed" })
            {
                data.TryGetProperty(field, out _).ShouldBeTrue($"{tool.ProtocolTool.Name} output schema lacks data.{field}");
            }
        }
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
