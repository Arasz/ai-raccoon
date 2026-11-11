using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over IDecisionService, one per decision HTTP route, sharing its requests and refusals.</summary>
public sealed class DecisionTools(IDecisionService service, IToolGate gate)
{
    internal const string TnRegisterDecisionTask = "register_decision_task";
    internal const string TnRecordDecision = "record_decision";
    internal const string TnAmendDecision = "amend_decision";
    internal const string TnTransitionDecision = "transition_decision";
    internal const string TnSupersedeDecision = "supersede_decision";
    internal const string TnLinkDecisions = "link_decisions";
    internal const string TnUnlinkDecisions = "unlink_decisions";
    internal const string TnGetDecision = "get_decision";
    internal const string TnQueryDecisions = "query_decisions";
    internal const string TnFindDecisionPrecedents = "find_decision_precedents";
    internal const string TnGetDecisionGraph = "get_decision_graph";
    internal const string TnExportDecisions = "export_decisions";
    internal const string TnPreviewDecisions = "preview_decisions";

    private const string ProjectIdText = "The registered project id. Required: decision tools never infer it from the working directory.";
    private const string TaskIdText = "The registered task this change belongs to.";
    private const string SessionIdText = "Your actual session id.";
    private const string RequestIdText = "A unique id for this command; resending the same command with it replays the first result.";
    private const string ActorText = "Who made the change; defaults to the session id. Recorded, never used for access.";

    /// <summary>
    ///     The SDK's options with the decision enums' snake_case converters ahead of its own enum converter, which
    ///     would otherwise win over the types' attributes and refuse <c>depends_on</c>. Nulls are written, because
    ///     the output schema lists nullable properties as required.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Insert(0, new DecisionEnumConverter<DecisionEdgeKind>());
        options.Converters.Insert(0, new DecisionEnumConverter<DecisionStatus>());
        options.Converters.Insert(0, new DecisionEnumConverter<DecisionDirection>());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>The decision tool names, read from this class's tool attributes.</summary>
    internal static IReadOnlySet<string> Names { get; } = typeof(DecisionTools).GetMethods()
        .Select(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), false).OfType<McpServerToolAttribute>().SingleOrDefault()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Each decision tool's argument names, read from its method's parameters.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Arguments { get; } = typeof(DecisionTools).GetMethods()
        .Select(method => (Method: method, method.GetCustomAttribute<McpServerToolAttribute>(false)?.Name))
        .Where(tool => tool.Name is not null)
        .ToDictionary(
            tool => tool.Name!,
            tool => (IReadOnlySet<string>)tool.Method.GetParameters()
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Select(parameter => parameter.Name!)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    /// <summary>
    ///     The CallToolFilter that refuses a decision tool call naming an argument the tool does not take, as the
    ///     HTTP routes refuse an unknown body field, instead of letting the SDK drop it.
    /// </summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> UnknownArgumentFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            if (request.Params is { Arguments: { } arguments } call
                && Arguments.TryGetValue(call.Name, out var declared)
                && arguments.Keys.FirstOrDefault(name => !declared.Contains(name)) is { } unknown)
            {
                throw new ArgumentException($"{call.Name} does not take '{unknown}'; its arguments are {string.Join(", ", declared)}.");
            }

            return next(request, cancellationToken);
        };

    [McpServerTool(Name = TnRegisterDecisionTask, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Registers a task before its first decision. Task metadata is immutable; the server assigns its order.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionTask>>> RegisterTask(
        [Description(ProjectIdText)] string projectId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("Short task title.")] string title,
        [Description("Optional link to the task in a tracker.")] string? sourceRef = null,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnRegisterDecisionTask, cancellationToken);
        var result = await new RegisterTaskRequest(taskId, sessionId, requestId, title, sourceRef, actor).SendAsync(service, canonical, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnRecordDecision, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Records a new decision under a registered task and returns its id and version 1.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionVersion>>> Record(
        [Description(ProjectIdText)] string projectId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("Short decision title.")] string title,
        [Description("Decision category, e.g. architecture.")] string category,
        [Description("The situation the decision answers.")] string scenario,
        [Description("What was decided.")] string outcome,
        [Description("Why it was decided.")] string reasoning,
        [Description("Initial status: proposed (default) or accepted.")] DecisionStatus status = DecisionStatus.Proposed,
        [Description("Confidence from 0 to 1.")] double? confidence = null,
        [Description("Who made the decision.")] string? maker = null,
        [Description("Alternatives considered (up to 20).")] IReadOnlyList<string>? alternatives = null,
        [Description("Expected consequences (up to 20).")] IReadOnlyList<string>? consequences = null,
        [Description("Evidence sources (up to 20), each with uri and description.")] IReadOnlyList<DecisionSource>? sources = null,
        [Description("When the decision starts to apply (ISO 8601).")] DateTimeOffset? validFrom = null,
        [Description("When the decision stops applying (ISO 8601).")] DateTimeOffset? validUntil = null,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnRecordDecision, cancellationToken);
        var result = await new RecordRequest(taskId, sessionId, requestId, title, category, scenario, outcome, reasoning, status, confidence, maker,
            alternatives, consequences, sources, validFrom, validUntil, actor).SendAsync(service, canonical, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnAmendDecision, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Replaces a decision's content with a new version; identity and status stay. Refuses a stale expectedVersion.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionVersion>>> Amend(
        [Description(ProjectIdText)] string projectId,
        [Description("The decision to amend.")] string decisionId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("The version you last read.")] int expectedVersion,
        [Description("Why the content changes.")] string changeReason,
        [Description("The complete replacement content (title, category, scenario, outcome, reasoning and optional fields).")] DecisionContent content,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnAmendDecision, cancellationToken);
        var result = await new AmendRequest(taskId, sessionId, requestId, expectedVersion, changeReason, content, actor)
            .SendAsync(service, canonical, decisionId, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnTransitionDecision, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Moves a decision to accepted, rejected or deprecated when the transition is legal. Use supersede_decision to replace one.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionVersion>>> Transition(
        [Description(ProjectIdText)] string projectId,
        [Description("The decision to transition.")] string decisionId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("The version you last read.")] int expectedVersion,
        [Description("accepted, rejected or deprecated.")] DecisionStatus targetStatus,
        [Description("Why the status changes.")] string changeReason,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnTransitionDecision, cancellationToken);
        var result = await new TransitionRequest(taskId, sessionId, requestId, expectedVersion, targetStatus, changeReason, actor)
            .SendAsync(service, canonical, decisionId, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnSupersedeDecision, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Replaces one accepted decision with another in one commit: the replaced one becomes superseded and a permanent edge links them.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionSupersession>>> Supersede(
        [Description(ProjectIdText)] string projectId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("The accepted decision that replaces the other.")] string replacementId,
        [Description("The accepted decision being replaced.")] string replacedId,
        [Description("The replacement's version you last read.")] int expectedReplacementVersion,
        [Description("The replaced decision's version you last read.")] int expectedReplacedVersion,
        [Description("Why it is replaced.")] string reason,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnSupersedeDecision, cancellationToken);
        var result = await new SupersedeRequest(taskId, sessionId, requestId, replacementId, replacedId, expectedReplacementVersion,
            expectedReplacedVersion, reason, actor).SendAsync(service, canonical, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnLinkDecisions, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Adds a typed edge between two decisions and returns the edge identity. Refuses a stale expectedProjectRevision.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionEdge>>> Link(
        [Description(ProjectIdText)] string projectId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("The decision the edge starts at.")] string sourceId,
        [Description("The decision the edge points to.")] string targetId,
        [Description("depends_on, supports or contradicts.")] DecisionEdgeKind kind,
        [Description("Why the decisions are linked.")] string reason,
        [Description("The project revision you last read.")] long expectedProjectRevision,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnLinkDecisions, cancellationToken);
        var result = await new LinkRequest(taskId, sessionId, requestId, sourceId, targetId, kind, reason, expectedProjectRevision, actor)
            .SendAsync(service, canonical, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnUnlinkDecisions, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Removes an edge while keeping it in history. Supersession edges are permanent and refused.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionEdge>>> Unlink(
        [Description(ProjectIdText)] string projectId,
        [Description("The edge to remove.")] string edgeId,
        [Description(TaskIdText)] string taskId,
        [Description(SessionIdText)] string sessionId,
        [Description(RequestIdText)] string requestId,
        [Description("Why the edge is removed.")] string reason,
        [Description("The project revision you last read.")] long expectedProjectRevision,
        [Description(ActorText)] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Write, TnUnlinkDecisions, cancellationToken);
        var result = await new UnlinkRequest(taskId, sessionId, requestId, reason, expectedProjectRevision, actor)
            .SendAsync(service, canonical, edgeId, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnGetDecision, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Returns one decision at its current or a requested version, optionally with its version history.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionDetails>>> Get(
        [Description(ProjectIdText)] string projectId,
        [Description("The decision to read.")] string decisionId,
        [Description("A specific version; the current one when omitted.")] int? version = null,
        [Description("Include every version.")] bool includeHistory = false,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnGetDecision, cancellationToken);
        var result = await service.GetAsync(canonical, decisionId, version, includeHistory, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnQueryDecisions, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Searches current decisions by literal text, category, status and task selector, a page at a time ordered by id.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionQueryResult>>> Query(
        [Description(ProjectIdText)] string projectId,
        [Description("Literal search text (1-2000 chars); omit to list by id.")] string? query = null,
        [Description("Only this category.")] string? category = null,
        [Description("Only these statuses; all when omitted.")] IReadOnlyList<DecisionStatus>? statuses = null,
        [Description("Tasks to search: one of last (newest N tasks), all, or taskIds. The whole project when omitted.")] DecisionTaskSelector? selector = null,
        [Description("Page size, 1-100.")] int limit = 20,
        [Description("Return decisions after this id (from nextAfterId).")] string? afterId = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnQueryDecisions, cancellationToken);
        var result = await service.QueryAsync(canonical, new DecisionQuery(query, category, statuses, selector, limit, afterId), cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnFindDecisionPrecedents, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Finds accepted, currently valid decisions ranked by text match to a scenario. You judge whether each applies.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionQueryResult>>> Precedents(
        [Description(ProjectIdText)] string projectId,
        [Description("The situation to match (1-2000 chars).")] string scenario,
        [Description("Only this category.")] string? category = null,
        [Description("How many, 1-20.")] int limit = 5,
        [Description("Include proposed, rejected, superseded, deprecated and expired decisions.")] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnFindDecisionPrecedents, cancellationToken);
        var result = await new PrecedentsRequest(scenario, category, limit, includeInactive).SendAsync(service, canonical, cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnGetDecisionGraph, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Walks the edges around one decision and returns nodes, typed edges and what was truncated.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionGraph>>> Graph(
        [Description(ProjectIdText)] string projectId,
        [Description("The decision to start from.")] string decisionId,
        [Description("upstream, downstream or both.")] DecisionDirection direction = DecisionDirection.Upstream,
        [Description("How many hops, 0-10.")] int depth = 2,
        [Description("Most nodes returned, 1-500.")] int maxNodes = 100,
        [Description("Also follow supports and contradicts edges.")] bool includeContext = false,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnGetDecisionGraph, cancellationToken);
        var result = await service.GraphAsync(canonical, new DecisionGraphRequest(decisionId, direction, depth, maxNodes, includeContext), cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    [McpServerTool(Name = TnExportDecisions, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Returns the complete selected snapshot as JSON. An export over 1 MiB is refused with decision-export-too-large; use the 'ai-raccoon decisions export' command for it.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionExport>>> Export(
        [Description(ProjectIdText)] string projectId,
        [Description("Tasks to export: one of last (newest N tasks), all, or taskIds. The newest task when omitted.")] DecisionTaskSelector? selector = null,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnExportDecisions, cancellationToken);
        var result = await new SelectionRequest(selector).SendAsync(service, canonical, cancellationToken);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        DecisionRefusals.EnsureWithinMcpLimit(envelope, canonical, SerializerOptions);
        return envelope;
    }

    [McpServerTool(Name = TnPreviewDecisions, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [McpToolScope(ToolScope.Project)]
    [Description("Returns a compact text summary of the selected decisions and their direct links, with explicit omission counts.")]
    public async Task<ApiEnvelope<DecisionResult<DecisionPreview>>> Preview(
        [Description(ProjectIdText)] string projectId,
        [Description("Tasks to preview: one of last (newest N tasks), all, or taskIds. The newest task when omitted.")] DecisionTaskSelector? selector = null,
        [Description("Most decisions shown, 1-100.")] int maxDecisions = 20,
        [Description("Most edges shown, 0-200.")] int maxEdges = 30,
        [Description("Text budget in characters, 1000-50000.")] int maxChars = 12000,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(DecisionRequests.Explicit(projectId), AccessRequirement.Read, TnPreviewDecisions, cancellationToken);
        var result = await service.PreviewAsync(canonical, new DecisionPreviewOptions(selector, maxDecisions, maxEdges, maxChars), cancellationToken);
        return await gate.WrapAsync(canonical, result, cancellationToken);
    }
}
