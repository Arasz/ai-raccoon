using AiRaccoon.Tests.E2E;
using AiRaccoon.Tests.TestHelpers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     D1 over the wire: every registered tool that takes a project id refuses an unregistered
///     guid with <c>project-not-registered</c>, reads included. The tool list is derived from
///     <see cref="RegisteredTools.Methods" />, so a new tool joins the theory on its own and must
///     get a row in <see cref="MinimalArgs" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class UnregisteredIdRefusalTests(UnregisteredIdRefusalTests.Server server)
    : IClassFixture<UnregisteredIdRefusalTests.Server>
{
    private static readonly string Unregistered = Guid.CreateVersion7().ToString("D");

    /// <summary>The smallest valid arguments per tool, besides the project id, so the gate is what refuses.</summary>
    private static readonly Dictionary<string, Dictionary<string, object?>> MinimalArgs = new(StringComparer.Ordinal)
    {
        ["get_entry"] = new() { ["hash"] = new string('a', 64), ["entryType"] = "memory" },
        ["add_memory"] = new() { ["content"] = "x" },
        ["query"] = new() { ["query"] = "x", ["sessionId"] = "sess-refusal" },
        ["get_quality_report"] = new(),
        ["get_index"] = new(),
        ["get_store_statistics"] = new(),
        ["delete_entry"] = new() { ["hash"] = new string('a', 64) },
        ["delete_context"] = new() { ["context"] = "ctx" },
        ["ingest_file"] = new() { ["path"] = Path.Combine(Path.GetTempPath(), "refusal.md") },
        ["ingest_directory"] = new() { ["path"] = Path.GetTempPath() },
        ["embed_deferred_entries"] = new(),
        ["get_engine_statistics"] = new(),
        ["get_memories_sharing_ranking"] = new(),
        ["delete_memory_from_ranking"] = new(),
        ["set_memory_usage_record"] = new() { ["correlationId"] = "corr", ["filePath"] = "a.md" },
        ["set_query_result_grade"] = new() { ["correlationId"] = "corr", ["grade"] = 1 },
        ["share_memory"] = new() { ["hash"] = new string('a', 64) },
        ["trigger_memory_sharing"] = new(),
        ["trigger_memory_cleanup"] = new(),
        ["set_memory_ttl"] = new() { ["hash"] = new string('a', 64), ["ttlDays"] = 1 },
        ["sync_to_cloud"] = new(),
        ["add_watch"] = new() { ["path"] = Path.GetTempPath() },
        ["get_watch_status"] = new(),
        ["remove_watch"] = new() { ["path"] = Path.GetTempPath() },
        ["initialize_workspace"] = new(),
        ["get_workspace_status"] = new() { ["workspaceId"] = "ws-1" },
        ["close_workspace"] = new() { ["workspaceId"] = "ws-1", ["keep"] = Array.Empty<string>() },
        ["discard_workspace"] = new() { ["workspaceId"] = "ws-1" }
    };

    private static IReadOnlyList<string> ProjectIdTools() =>
    [
        .. RegisteredTools.Methods()
            .Where(t => t.Method.GetParameters().Any(p => p.Name is "projectId" or "projectIds"))
            .Select(t => t.Attr.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
    ];

    public static TheoryData<string> Tools()
    {
        var data = new TheoryData<string>();
        foreach (var name in ProjectIdTools())
        {
            data.Add(name);
        }

        return data;
    }

    [RetryFact]
    public void MinimalArgsTable_CoversEveryProjectIdTool()
    {
        MinimalArgs.Keys.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(ProjectIdTools());
    }

    [RetryTheory]
    [MemberData(nameof(Tools))]
    public async Task EveryProjectIdTool_RefusesAnUnregisteredGuid(string toolName)
    {
        var arguments = new Dictionary<string, object?>(MinimalArgs[toolName]);
        if (toolName == "trigger_memory_sharing")
        {
            arguments["projectIds"] = new[] { Unregistered };
        }
        else
        {
            arguments["projectId"] = Unregistered;
        }

        var result = await server.Client.CallToolAsync(toolName, arguments,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true, $"{toolName} accepted an unregistered project id");
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))
            .ShouldStartWith("project-not-registered:", customMessage: toolName);
    }

    /// <summary>One server for the class, nothing registered.</summary>
    public sealed class Server : IAsyncLifetime
    {
        private readonly McpServerFactory _factory = new();

        public McpClient Client { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Client = await _factory.CreateClientAsync();

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }
}
