using Xunit;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     One row per decision operation in docs/plans/decision-graph/api.md: the MCP tool, the HTTP
///     route and the smallest arguments the gate sees before the service does. Route placeholders
///     are filled from the same arguments, so HTTP and MCP calls carry identical inputs.
/// </summary>
public sealed record DecisionOperation(string Tool, string Method, string Route, bool Write, IReadOnlyDictionary<string, object?> Args)
{
    private static readonly string[] RouteParameters = ["decisionId", "edgeId"];

    /// <summary>The request path for <paramref name="projectId" />, route placeholders filled from <see cref="Args" />.</summary>
    public string Path(string projectId)
    {
        var path = $"/decisions/projects/{Uri.EscapeDataString(projectId)}{Route}";
        foreach (var name in RouteParameters.Where(Args.ContainsKey))
        {
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString((string)Args[name]!), StringComparison.Ordinal);
        }

        return Method == "GET" ? path + Query() : path;
    }

    /// <summary>The JSON body: every argument that is not a route placeholder.</summary>
    public Dictionary<string, object?> Body() =>
        Args.Where(a => !RouteParameters.Contains(a.Key) || !Route.Contains($"{{{a.Key}}}", StringComparison.Ordinal))
            .ToDictionary(a => a.Key, a => a.Value);

    /// <summary>The MCP arguments for <paramref name="projectId" />.</summary>
    public Dictionary<string, object?> McpArgs(string projectId) =>
        new(Args) { ["projectId"] = projectId };

    private string Query()
    {
        var query = Body().Select(a => $"{a.Key}={Uri.EscapeDataString(Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture)!.ToLowerInvariant())}").ToArray();
        return query.Length == 0 ? "" : $"?{string.Join('&', query)}";
    }
}

public static class DecisionOperations
{
    private static Dictionary<string, object?> Write(string requestId, Dictionary<string, object?> rest)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["taskId"] = "task", ["sessionId"] = "session", ["requestId"] = requestId
        };
        foreach (var (key, value) in rest)
        {
            args[key] = value;
        }

        return args;
    }

    private static Dictionary<string, object?> Content() => new()
    {
        ["title"] = "Title", ["category"] = "architecture", ["scenario"] = "Scenario", ["outcome"] = "Outcome", ["reasoning"] = "Reasoning"
    };

    /// <summary>Every operation, in api.md table order.</summary>
    public static IReadOnlyList<DecisionOperation> All { get; } =
    [
        new("register_decision_task", "POST", "/tasks", true, Write("register", new() { ["title"] = "Task" })),
        new("record_decision", "POST", "/records", true, Write("record", Content())),
        new("amend_decision", "POST", "/records/{decisionId}/amend", true,
            Write("amend", new() { ["decisionId"] = "missing", ["expectedVersion"] = 1, ["changeReason"] = "why", ["content"] = Content() })),
        new("transition_decision", "POST", "/records/{decisionId}/transition", true,
            Write("transition", new() { ["decisionId"] = "missing", ["expectedVersion"] = 1, ["targetStatus"] = "accepted", ["changeReason"] = "why" })),
        new("supersede_decision", "POST", "/supersessions", true,
            Write("supersede", new()
            {
                ["replacementId"] = "missing-a", ["replacedId"] = "missing-b", ["expectedReplacementVersion"] = 1,
                ["expectedReplacedVersion"] = 1, ["reason"] = "why"
            })),
        new("link_decisions", "POST", "/edges", true,
            Write("link", new()
            {
                ["sourceId"] = "missing-a", ["targetId"] = "missing-b", ["kind"] = "supports", ["reason"] = "why", ["expectedProjectRevision"] = 0
            })),
        new("unlink_decisions", "POST", "/edges/{edgeId}/remove", true,
            Write("unlink", new() { ["edgeId"] = "missing", ["reason"] = "why", ["expectedProjectRevision"] = 0 })),
        new("get_decision", "GET", "/records/{decisionId}", false, new Dictionary<string, object?> { ["decisionId"] = "missing" }),
        new("query_decisions", "POST", "/query", false, new Dictionary<string, object?>()),
        new("find_decision_precedents", "POST", "/precedents", false, new Dictionary<string, object?> { ["scenario"] = "persistence" }),
        new("get_decision_graph", "POST", "/graph", false, new Dictionary<string, object?> { ["decisionId"] = "missing" }),
        new("export_decisions", "POST", "/export", false, new Dictionary<string, object?>()),
        new("preview_decisions", "POST", "/preview", false, new Dictionary<string, object?>())
    ];

    /// <summary>HTTP routes with no MCP tool: Tool is the gate operation name, not a registered tool.</summary>
    public static IReadOnlyList<DecisionOperation> HttpOnly { get; } =
    [
        new("import-semantica", "POST", "/imports/semantica/preview", false, new Dictionary<string, object?> { ["source"] = "", ["mapping"] = "" }),
        new("import-semantica", "POST", "/imports/semantica/apply", true, new Dictionary<string, object?>
        {
            ["sessionId"] = "session", ["requestId"] = "import", ["source"] = "", ["mapping"] = "", ["sourceDigest"] = "A",
            ["mappingDigest"] = "B", ["expectedProjectRevision"] = 0
        })
    ];

    public static TheoryData<string> Tools() => [.. All.Select(o => o.Tool)];

    public static TheoryData<string> WriteTools() => [.. All.Where(o => o.Write).Select(o => o.Tool)];

    public static DecisionOperation Named(string tool) => All.Single(o => o.Tool == tool);
}
