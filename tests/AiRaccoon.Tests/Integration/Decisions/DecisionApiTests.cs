using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>
///     The decision operations through both adapters on one host: each api.md row has one HTTP
///     route and one MCP tool, the tools declare all four annotations on the wire, and both
///     adapters refuse the same call with the same kind.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionApiTests : IAsyncLifetime
{
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private DecisionHost _host = null!;
    private McpClient _mcp = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await DecisionHost.StartAsync("decision-api", Ct);
        await _host.RegisterAsync(_project, Ct);
        _mcp = await _host.McpAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [RetryFact]
    public void OperationTable_MatchesTheDecisionToolClass()
    {
        RegisteredTools.Methods().Where(t => t.Class == typeof(DecisionTools)).Select(t => t.Attr.Name!).Order(StringComparer.Ordinal)
            .ShouldBe(DecisionOperations.All.Select(o => o.Tool).Order(StringComparer.Ordinal));
    }

    [RetryFact]
    public void HttpRoutes_MapEveryOperationOnce()
    {
        var routes = _host.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/decisions/projects/", StringComparison.Ordinal))
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText!["/decisions/projects/{projectId}".Length..]}")
            .Order(StringComparer.Ordinal);

        routes.ShouldBe(DecisionOperations.All.Concat(DecisionOperations.HttpOnly).Select(o => $"{o.Method} {o.Route}").Order(StringComparer.Ordinal));
    }

    /// <summary>Read from the raw tools/list JSON: a false hint the SDK dropped would be absent here.</summary>
    [RetryFact]
    public async Task ToolsList_DeclaresEveryAnnotationExplicitly()
    {
        var response = await _mcp.SendRequestAsync(new JsonRpcRequest { Method = RequestMethods.ToolsList }, Ct);
        var tools = response.Result!["tools"]!.AsArray().ToDictionary(t => (string)t!["name"]!, t => t!);

        foreach (var operation in DecisionOperations.All)
        {
            tools.ShouldContainKey(operation.Tool);
            var annotations = tools[operation.Tool]["annotations"]!.AsObject();
            var expected = new Dictionary<string, bool>
            {
                ["readOnlyHint"] = !operation.Write, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false
            };
            foreach (var (hint, value) in expected)
            {
                annotations.ContainsKey(hint).ShouldBeTrue($"{operation.Tool} omits {hint}");
                annotations[hint]!.GetValue<bool>().ShouldBe(value, $"{operation.Tool}.{hint}");
            }

            var schema = tools[operation.Tool]["inputSchema"]!;
            var required = schema["required"]!.AsArray().Select(n => (string)n!).ToHashSet(StringComparer.Ordinal);
            required.ShouldContain("projectId", operation.Tool);
            schema["properties"]!.AsObject().Select(p => p.Key).ShouldBeSubsetOf(
                operation.Args.Keys.Concat(OptionalInputs).Append("projectId"), operation.Tool);
            tools[operation.Tool]["outputSchema"].ShouldNotBeNull(operation.Tool);
        }
    }

    private static readonly string[] OptionalInputs =
    [
        "sourceRef", "actor", "status", "confidence", "maker", "alternatives", "consequences", "sources", "validFrom", "validUntil",
        "version", "includeHistory", "query", "category", "statuses", "selector", "limit", "afterId", "includeInactive",
        "direction", "depth", "maxNodes", "includeContext", "maxDecisions", "maxEdges", "maxChars"
    ];

    /// <summary>
    ///     Read-only mode, an unregistered project and a padded id: HTTP and MCP refuse each with the
    ///     same kind, and a write is refused exactly where committed writes are prohibited.
    /// </summary>
    [RetryTheory]
    [MemberData(nameof(DecisionOperations.Tools), MemberType = typeof(DecisionOperations))]
    public async Task DecisionApi_AnnotationsAndGuardParity(string tool)
    {
        var operation = DecisionOperations.Named(tool);

        var unregistered = Guid.CreateVersion7().ToString("D");
        await AssertParityAsync(operation, unregistered, "project-not-registered");
        await AssertParityAsync(operation, $" {_project}", "invalid-argument");
        (await _host.McpAsync(_mcp, tool, operation.McpArgs(""), Ct)).Kind.ShouldBe("invalid-argument");

        await _host.SetModeAsync(_project, "ro", Ct);
        var http = await _host.HttpAsync(operation, _project, Ct);
        var mcp = await _host.McpAsync(_mcp, tool, operation.McpArgs(_project), Ct);
        mcp.Kind.ShouldBe(http.Kind, $"{tool}: http {http.Body} / mcp {mcp.Body}");
        if (operation.Write)
        {
            http.Kind.ShouldBe("access-denied", http.Body);
        }
        else
        {
            http.Kind.ShouldNotBe("access-denied", http.Body);
        }
    }

    /// <summary>An argument the operation does not declare is refused on both adapters instead of being dropped.</summary>
    [RetryTheory]
    [MemberData(nameof(DecisionOperations.Tools), MemberType = typeof(DecisionOperations))]
    public async Task UnknownArgument_IsRefusedOnBothAdapters(string tool)
    {
        var template = DecisionOperations.Named(tool);
        var operation = template with { Args = new Dictionary<string, object?>(template.Args) { ["maekr"] = "someone" } };

        var mcp = await _host.McpAsync(_mcp, tool, operation.McpArgs(_project), Ct);

        mcp.Kind.ShouldBe("invalid-argument", mcp.Body);
        mcp.Body.ShouldContain("maekr");
        if (operation.Method == "POST")
        {
            var http = await _host.HttpAsync(operation, _project, Ct);
            http.Status.ShouldBe(400, http.Body);
            http.Kind.ShouldBe("invalid-argument", http.Body);
            http.Body.ShouldContain("maekr");
        }
    }

    /// <summary>The service sees the gate's canonical id through MCP too.</summary>
    [RetryFact]
    public async Task NonCanonicalId_ReachesTheServiceCanonical()
    {
        var respelled = $"{{{_project.ToUpperInvariant()}}}";
        var register = await Mcp("register_decision_task", respelled);
        var record = await Mcp("record_decision", respelled);

        register.Kind.ShouldBe("ok", register.Body);
        ProjectIdOf(register.Body).ShouldBe(_project);
        ProjectIdOf(record.Body).ShouldBe(_project);
        var id = DecisionIdOf(record.Body);
        var get = await _host.McpAsync(_mcp, "get_decision", new Dictionary<string, object?> { ["projectId"] = _project, ["decisionId"] = id }, Ct);
        get.Kind.ShouldBe("ok", get.Body);
    }

    [RetryFact]
    public async Task DomainRefusals_HaveTheSameKindOnBothAdapters()
    {
        (await Mcp("register_decision_task", _project)).Kind.ShouldBe("ok");
        var id = DecisionIdOf((await Mcp("record_decision", _project)).Body);

        await AssertDomainAsync("get_decision", new() { ["decisionId"] = "missing" }, "decision-not-found", 404);
        var stale = await AssertDomainAsync("transition_decision", Write("stale", new()
        {
            ["decisionId"] = id, ["expectedVersion"] = 2, ["targetStatus"] = "accepted", ["changeReason"] = "why"
        }), "decision-conflict", 409);
        stale.Mcp.Body.ShouldContain("currentVersion=1");
        JsonDocument.Parse(stale.Http.Body).RootElement.GetProperty("currentVersion").GetInt32().ShouldBe(1);
        var otherArgs = DecisionOperations.Named("record_decision").McpArgs(_project);
        otherArgs["requestId"] = "other";
        var otherId = DecisionIdOf((await _host.McpAsync(_mcp, "record_decision", otherArgs, Ct)).Body);
        var revision = JsonDocument.Parse((await Mcp("export_decisions", _project)).Body).RootElement.GetProperty("data").GetProperty("projectRevision").GetInt64();
        var staleLink = await AssertDomainAsync("link_decisions", Write("stale-link", new()
        {
            ["sourceId"] = id, ["targetId"] = otherId, ["kind"] = "supports", ["reason"] = "why", ["expectedProjectRevision"] = revision - 1
        }), "decision-conflict", 409);
        staleLink.Mcp.Body.ShouldContain($"currentProjectRevision={revision}");
        JsonDocument.Parse(staleLink.Http.Body).RootElement.GetProperty("currentProjectRevision").GetInt64().ShouldBe(revision);
        await AssertDomainAsync("transition_decision", Write("illegal", new()
        {
            ["decisionId"] = id, ["expectedVersion"] = 1, ["targetStatus"] = "deprecated", ["changeReason"] = "why"
        }), "decision-invalid-transition", 409);
        await AssertDomainAsync("query_decisions", new() { ["limit"] = 0 }, "invalid-argument", 400);
        await AssertDomainAsync("transition_decision", Write("bad-enum", new()
        {
            ["decisionId"] = id, ["expectedVersion"] = 1, ["targetStatus"] = "sideways", ["changeReason"] = "why"
        }), "invalid-argument", 400);
    }

    [RetryFact]
    public async Task Success_ReturnsTheEnvelopeAsStructuredContentAndText()
    {
        var result = await _mcp.CallToolAsync("register_decision_task", DecisionOperations.Named("register_decision_task").McpArgs(_project), cancellationToken: Ct);

        result.IsError.ShouldNotBe(true);
        result.StructuredContent.ShouldNotBeNull();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(result.StructuredContent!.Value.GetRawText())).ShouldBeTrue();
        result.StructuredContent!.Value.GetProperty("data").GetProperty("value").GetProperty("taskId").GetString().ShouldBe("task");
    }

    /// <summary>MCP refuses an export over its 1 MiB cap with CLI guidance and no partial content; HTTP returns it whole.</summary>
    [RetryFact]
    public async Task Export_OverTheMcpCap_IsRefusedOverMcpAndServedOverHttp()
    {
        var root = $"/decisions/projects/{_project}";
        (await _host.Http.PostAsJsonAsync(root + "/tasks", new { taskId = "big", sessionId = "s", requestId = "big", title = "Big" }, Ct)).EnsureSuccessStatusCode();
        var text = new string('x', 16000);
        for (var i = 0; i < 25; i++)
        {
            (await _host.Http.PostAsJsonAsync(root + "/records", new
            {
                taskId = "big", sessionId = "s", requestId = $"big-{i}", title = $"Decision {i}", category = "architecture",
                scenario = text, outcome = text, reasoning = text
            }, Ct)).EnsureSuccessStatusCode();
        }

        var mcp = await _host.McpAsync(_mcp, "export_decisions", new Dictionary<string, object?> { ["projectId"] = _project }, Ct);
        var http = await _host.HttpAsync(DecisionOperations.Named("export_decisions"), _project, Ct);

        mcp.Kind.ShouldBe("decision-export-too-large", mcp.Body[..Math.Min(200, mcp.Body.Length)]);
        mcp.Body.ShouldContain("ai-raccoon decisions export");
        mcp.Body.Length.ShouldBeLessThan(2000);
        http.Kind.ShouldBe("ok");
        http.Body.Length.ShouldBeGreaterThan(1024 * 1024);
        JsonDocument.Parse(http.Body).RootElement.GetProperty("data").GetProperty("value").GetProperty("decisions").GetArrayLength().ShouldBe(25);
    }

    private async Task AssertParityAsync(DecisionOperation operation, string projectId, string kind)
    {
        var http = await _host.HttpAsync(operation, projectId, Ct);
        var mcp = await _host.McpAsync(_mcp, operation.Tool, operation.McpArgs(projectId), Ct);
        http.Kind.ShouldBe(kind, $"{operation.Tool} http: {http.Body}");
        mcp.Kind.ShouldBe(kind, $"{operation.Tool} mcp: {mcp.Body}");
    }

    public static TheoryData<int, int, string, int> GraphBounds() => new()
    {
        { 11, 100, "invalid-argument", 400 },
        { -1, 100, "invalid-argument", 400 },
        { 2, 0, "invalid-argument", 400 },
        { 2, 501, "invalid-argument", 400 },
        { 10, 100, "ok", 200 },
        { 0, 500, "ok", 200 },
        { 2, 1, "ok", 200 }
    };

    /// <summary>Graph depth is 0..10 and maxNodes 1..500 on both adapters; just past either bound is invalid, at it is accepted.</summary>
    [RetryTheory]
    [MemberData(nameof(GraphBounds))]
    public async Task GraphBounds_AreTheSameOnBothAdapters(int depth, int maxNodes, string kind, int status)
    {
        (await Mcp("register_decision_task", _project)).Kind.ShouldBe("ok");
        var id = DecisionIdOf((await Mcp("record_decision", _project)).Body);

        await AssertDomainAsync("get_decision_graph", new() { ["decisionId"] = id, ["depth"] = depth, ["maxNodes"] = maxNodes }, kind, status);
    }

    private async Task<(DecisionHost.Outcome Http, DecisionHost.Outcome Mcp)> AssertDomainAsync(string tool, Dictionary<string, object?> args, string kind, int status)
    {
        var template = DecisionOperations.Named(tool);
        var operation = template with { Args = args };
        var http = await _host.HttpAsync(operation, _project, Ct);
        var mcp = await _host.McpAsync(_mcp, tool, operation.McpArgs(_project), Ct);
        http.Status.ShouldBe(status, http.Body);
        http.Kind.ShouldBe(kind, http.Body);
        mcp.Kind.ShouldBe(kind, mcp.Body);
        return (http, mcp);
    }

    private static Dictionary<string, object?> Write(string requestId, Dictionary<string, object?> rest)
    {
        rest["taskId"] = "task";
        rest["sessionId"] = "session";
        rest["requestId"] = requestId;
        return rest;
    }

    private Task<DecisionHost.Outcome> Mcp(string tool, string projectId) =>
        _host.McpAsync(_mcp, tool, DecisionOperations.Named(tool).McpArgs(projectId), Ct);

    private static string? ProjectIdOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("projectId").GetString();

    private static string DecisionIdOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("value").GetProperty("decisionId").GetString()!;
}
