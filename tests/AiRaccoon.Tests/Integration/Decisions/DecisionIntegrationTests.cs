using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>
///     The whole decision flow across HTTP, MCP and the CLI on a real host, then again on a second
///     host over the same bank: what was written survives the restart, and each access mode lets the
///     three adapters read alike and refuse or accept the same writes.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionIntegrationTests : IAsyncLifetime
{
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private readonly string _root = TestData.CreateTempRoot("decision-integration");
    private readonly List<IAsyncDisposable> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => $"/decisions/projects/{_project}";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        TestData.DeleteTempRoot(_root);
    }

    public static TheoryData<string, bool> Modes() => new()
    {
        { "ro", false },
        { "rw", true },
        { "full", true }
    };

    [RetryTheory]
    [MemberData(nameof(Modes))]
    public async Task DecisionIntegration_RestartAndAccessModes(string mode, bool writes)
    {
        var first = await OpenAsync();
        await first.RegisterAsync(_project, Ct);
        var firstMcp = await first.McpAsync(Ct);

        (await Mcp(first, firstMcp, "register_decision_task", new() { ["taskId"] = "t1", ["requestId"] = "t1", ["title"] = "First" })).ShouldBeOk();
        var stored = await RecordOverHttpAsync(first, "t1", "Store decisions in the bank");
        var bank = Id((await Mcp(first, firstMcp, "record_decision", Content("t1", "Keep one bank", "r-bank"))).Body);
        (await Mcp(first, firstMcp, "link_decisions", new()
        {
            ["taskId"] = "t1", ["requestId"] = "link", ["sourceId"] = stored, ["targetId"] = bank, ["kind"] = "depends_on", ["reason"] = "one file",
            ["expectedProjectRevision"] = (await ExportAsync(first, "{}"))["projectRevision"]!.GetValue<long>()
        })).ShouldBeOk();
        var (registered, _, registerErr) = await Cli(first, ["task", "register", "--project-id", _project, "--task-id", "t2", "--title", "Second"]);
        registered.ShouldBe(0, registerErr);
        var cli = Id((await RecordOverCliAsync(first, "t2", "Expose a CLI")).Out, "value");
        await first.SetModeAsync(_project, mode, Ct);
        var before = await ExportAsync(first, """{"selector":{"all":true}}""");
        await RestartAsync(first);

        var second = await OpenAsync();
        var mcp = await second.McpAsync(Ct);

        var after = await ExportAsync(second, """{"selector":{"all":true}}""");
        JsonNode.DeepEquals(Without(after["value"]!, "exportedAt"), Without(before["value"]!, "exportedAt")).ShouldBeTrue(
            $"the bank lost or changed decisions across the restart\nbefore: {before}\nafter: {after}");
        after["projectRevision"]!.GetValue<long>().ShouldBe(before["projectRevision"]!.GetValue<long>());

        var get = await ReadAsync(second, mcp, "get_decision", new() { ["decisionId"] = stored }, ["get", stored, "--project-id", _project]);
        get.ToJsonString().ShouldContain("Store decisions in the bank");
        var query = (await ReadAsync(second, mcp, "query_decisions", new(), ["query", "--project-id", _project])).ToJsonString();
        foreach (var id in new[] { stored, bank, cli })
        {
            query.ShouldContain(id);
        }

        var graph = await ReadAsync(second, mcp, "get_decision_graph", new() { ["decisionId"] = stored }, null);
        var edges = graph["value"]!["edges"]!.AsArray();
        edges.Count.ShouldBe(1, graph.ToJsonString());
        edges[0]!.ToJsonString().ShouldContain(bank);
        edges[0]!.ToJsonString().ShouldContain("depends_on");

        foreach (var (selection, body, tasks) in Selections)
        {
            var (exit, stdout, stderr) = await Cli(second, ["export", "--project-id", _project, .. selection]);
            exit.ShouldBe(0, stderr);
            var exported = JsonNode.Parse(stdout)!.AsObject();
            var backend = await ExportAsync(second, body);
            exported["selectedTaskIds"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(tasks, ignoreOrder: true, string.Join(' ', selection));
            JsonNode.DeepEquals(Without(exported, "exportedAt", "projectId", "projectRevision"), Without(backend["value"]!, "exportedAt")).ShouldBeTrue(
                $"CLI export {string.Join(' ', selection)} differs from the backend's\nCLI: {stdout}\nbackend: {backend["value"]}");

            var (previewExit, previewOut, previewErr) = await Cli(second, ["preview", "--project-id", _project, "--format", "json", .. selection]);
            previewExit.ShouldBe(0, previewErr);
            var preview = await PostAsync(second, "/preview", body);
            JsonNode.DeepEquals(Without(JsonNode.Parse(previewOut)!, "projectId", "projectRevision"), preview["value"]).ShouldBeTrue(
                $"CLI preview {string.Join(' ', selection)} differs from the backend's\nCLI: {previewOut}\nbackend: {preview["value"]}");
        }

        var http = await second.HttpAsync(DecisionOperations.Named("register_decision_task") with { Args = TaskArgs("t-http") }, _project, Ct);
        var overMcp = await second.McpAsync(mcp, "register_decision_task", new Dictionary<string, object?>(TaskArgs("t-mcp")) { ["projectId"] = _project }, Ct);
        var (cliExit, cliOut, cliErr) = await Cli(second, ["task", "register", "--project-id", _project, "--task-id", "t-cli", "--title", "Refused?"]);
        var revision = (await ExportAsync(second, "{}"))["projectRevision"]!.GetValue<long>();
        if (writes)
        {
            http.Kind.ShouldBe("ok", http.Body);
            overMcp.Kind.ShouldBe("ok", overMcp.Body);
            cliExit.ShouldBe(0, cliErr);
            revision.ShouldBe(after["projectRevision"]!.GetValue<long>() + 3);
        }
        else
        {
            http.Kind.ShouldBe("access-denied", http.Body);
            overMcp.Kind.ShouldBe("access-denied", overMcp.Body);
            cliExit.ShouldBe(ErrorCode.Usage.RequestRejected, cliErr);
            cliErr.ShouldContain("access-denied");
            cliOut.ShouldBeEmpty();
            revision.ShouldBe(after["projectRevision"]!.GetValue<long>());
        }
    }

    /// <summary>CLI selection flags, the backend body that names the same selection, and the tasks it selects.</summary>
    private static readonly (string[] Selection, string Body, string[] Tasks)[] Selections =
    [
        ([], "{}", ["t2"]),
        (["--tasks", "2"], """{"selector":{"last":2}}""", ["t1", "t2"]),
        (["--all"], """{"selector":{"all":true}}""", ["t1", "t2"])
    ];

    private async Task<DecisionHost> OpenAsync()
    {
        var host = await DecisionHost.OpenAsync(_root, Ct);
        _hosts.Add(host);
        return host;
    }

    private async Task RestartAsync(DecisionHost host)
    {
        _hosts.Remove(host);
        await host.DisposeAsync();
    }

    private static Dictionary<string, object?> TaskArgs(string taskId) => new()
    {
        ["taskId"] = taskId, ["sessionId"] = "session", ["requestId"] = taskId, ["title"] = "Refused?"
    };

    private static Dictionary<string, object?> Content(string taskId, string title, string requestId) => new()
    {
        ["taskId"] = taskId, ["sessionId"] = "session", ["requestId"] = requestId, ["title"] = title, ["category"] = "architecture",
        ["scenario"] = "Scenario", ["outcome"] = "Outcome", ["reasoning"] = "Reasoning"
    };

    private Task<DecisionHost.Outcome> Mcp(DecisionHost host, McpClient client, string tool, Dictionary<string, object?> args)
    {
        args["projectId"] = _project;
        if (args.ContainsKey("requestId"))
        {
            args["sessionId"] = "session";
        }

        return host.McpAsync(client, tool, args, Ct);
    }

    /// <summary>One read sent over HTTP, MCP and, when it has a verb, the CLI: each must succeed with the same data.</summary>
    private async Task<JsonNode> ReadAsync(DecisionHost host, McpClient client, string tool, Dictionary<string, object?> args, string[]? cliArgs)
    {
        var http = await host.HttpAsync(DecisionOperations.Named(tool) with { Args = args }, _project, Ct);
        var overMcp = await Mcp(host, client, tool, new Dictionary<string, object?>(args));
        http.ShouldBeOk();
        overMcp.ShouldBeOk();
        var data = WithoutNulls(JsonNode.Parse(http.Body)!["data"]!);
        JsonNode.DeepEquals(WithoutNulls(JsonNode.Parse(overMcp.Body)!["data"]!), data).ShouldBeTrue($"{tool}: HTTP {http.Body}\nMCP {overMcp.Body}");
        if (cliArgs is not null)
        {
            var (exit, stdout, stderr) = await Cli(host, cliArgs);
            exit.ShouldBe(0, stderr);
            JsonNode.DeepEquals(WithoutNulls(JsonNode.Parse(stdout)!), data).ShouldBeTrue($"{tool}: HTTP {http.Body}\nCLI {stdout}");
        }

        return data;
    }

    private async Task<string> RecordOverHttpAsync(DecisionHost host, string taskId, string title)
    {
        var response = await host.Http.PostAsJsonAsync(Root + "/records", Content(taskId, title, "r-" + title), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(body);
        return Id(body);
    }

    private async Task<(int Exit, string Out, string Err)> RecordOverCliAsync(DecisionHost host, string taskId, string title)
    {
        var input = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(input,
            $$"""{"title":"{{title}}","category":"architecture","scenario":"Scenario","outcome":"Outcome","reasoning":"Reasoning"}""", Ct);
        var result = await Cli(host, ["record", "--project-id", _project, "--task-id", taskId, "--input", input]);
        result.Exit.ShouldBe(0, result.Err);
        return result;
    }

    private static async Task<(int Exit, string Out, string Err)> Cli(DecisionHost host, string[] args)
    {
        using var http = new HttpClient { BaseAddress = host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        var store = new LazyServerSettingsStore(_ =>
            Task.FromResult<ISettingsStore>(new ServerSettingsStore(http, DecisionHost.Token, CliSettingsBackend.RequestDeadline)));
        var commands = TestData.CreateConfigCommands(new FakeConfigStore(), decisions: new DecisionCommands(store));
        return await CliRun.RunAsync(["decisions", .. args], commands);
    }

    private Task<JsonObject> ExportAsync(DecisionHost host, string body) => PostAsync(host, "/export", body);

    private async Task<JsonObject> PostAsync(DecisionHost host, string route, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Http.PostAsync(Root + route, content, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(text);
        return JsonNode.Parse(text)!["data"]!.AsObject();
    }

    private static string Id(string body, string path = "data") =>
        (path == "data" ? JsonNode.Parse(body)!["data"]!["value"] : JsonNode.Parse(body)![path])!["decisionId"]!.GetValue<string>();

    /// <summary>A copy with null-valued properties removed: MCP omits them, HTTP writes them.</summary>
    private static JsonNode WithoutNulls(JsonNode node)
    {
        var copy = node.DeepClone();
        Strip(copy);
        return copy;

        static void Strip(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var name in obj.Where(p => p.Value is null).Select(p => p.Key).ToArray())
                    {
                        obj.Remove(name);
                    }

                    foreach (var (_, value) in obj)
                    {
                        Strip(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Strip(item);
                    }

                    break;
            }
        }
    }

    private static JsonNode Without(JsonNode node, params string[] names)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var name in names)
        {
            copy.Remove(name);
        }

        return copy;
    }
}

internal static class OutcomeAssertions
{
    internal static void ShouldBeOk(this DecisionHost.Outcome outcome) => outcome.Kind.ShouldBe("ok", outcome.Body);
}
