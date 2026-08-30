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
    // projectId is optional on every tool (cwd-default resolution): `string?` = nullable with a
    // null default; `string?`-with-`""` default reads as a plain optional string because C#
    // forbids an optional parameter before a required one (CS1737) — those tools carry a required
    // parameter after projectId and use an empty-string default instead.
    private const string ExpectedContract = """
                                            code_get(projectId:string?, hash:string!)
                                            memory_delete(projectId:string?, hash:string!)
                                            memory_delete_context(projectId:string?, context:string!)
                                            memory_embed_pending(projectId:string|null?, limit:integer|null?)
                                            memory_get(projectId:string?, hash:string!)
                                            memory_ingest_directory(projectId:string?, path:string!, context:string|null?)
                                            memory_ingest_file(projectId:string?, path:string!, context:string|null?)
                                            memory_list(projectId:string|null?)
                                            memory_performance(projectId:string|null?, windowMinutes:integer|null?, bucketMinutes:integer|null?)
                                            memory_promotion_discard(projectId:string|null?, hash:string|null?)
                                            memory_promotion_list(projectId:string|null?, limit:integer?, includeFullValue:boolean?, allProjects:boolean?)
                                            memory_record_followthrough(projectId:string?, correlationId:string!, filePath:string!)
                                            memory_record_grade(projectId:string?, correlationId:string!, grade:integer!, note:string|null?)
                                            memory_search(projectId:string?, query:string!, scope:string?, workspaceId:string|null?, limit:integer?, minRelativeScore:number?, rrfK:integer|null?, ftsWeight:integer|null?, vectorWeight:integer|null?, sourceLambda:number|null?, consolidationThreshold:number|null?, docScoreFormula:string|null?, candidateWindow:string|null?, contextLabel:string|null?, kind:string?, codeLimit:integer|null?, codeMinRelativeScore:number|null?)
                                            memory_set_ttl(projectId:string?, hash:string!, ttlDays:integer|null?)
                                            memory_share(projectId:string?, hash:string!)
                                            memory_share_extract(projectIds:array!, mode:string?, limit:integer|null?, includeTtlRows:boolean?, autoPromote:boolean?, confirm:boolean?)
                                            memory_stats(projectId:string|null?)
                                            memory_sweep(projectId:string|null?, dryRun:boolean?)
                                            memory_sync(projectId:string|null?)
                                            memory_watch_add(projectId:string?, path:string!)
                                            memory_watch_remove(projectId:string?, path:string!)
                                            memory_watch_status(projectId:string|null?)
                                            memory_workspace_begin(projectId:string|null?, agentId:string|null?, name:string|null?)
                                            memory_workspace_consolidate(projectId:string?, workspaceId:string!, keep:array!)
                                            memory_workspace_discard(projectId:string?, workspaceId:string!)
                                            memory_workspace_status(projectId:string?, workspaceId:string!)
                                            memory_write(projectId:string?, content:string!, workspaceId:string|null?, agentId:string|null?, context:string|null?, sourceFile:string|null?, section:string|null?)
                                            project_id_token_get(name:string|null?)
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
        var host = McpServerSetup.CreateServerHost(
            new ServerConfig(0, McpTransport.Stdio, TestData.CreateInfrastructureOptions(_dataRoot)));
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
