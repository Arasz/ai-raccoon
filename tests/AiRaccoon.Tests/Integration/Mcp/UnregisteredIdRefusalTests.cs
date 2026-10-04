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
        ["code_get"] = new() { ["hash"] = new string('a', 64) },
        ["memory_write"] = new() { ["content"] = "x" },
        ["memory_get"] = new() { ["hash"] = new string('a', 64) },
        ["memory_search"] = new() { ["query"] = "x", ["sessionId"] = "sess-refusal" },
        ["memory_list"] = new(),
        ["memory_stats"] = new(),
        ["memory_delete"] = new() { ["hash"] = new string('a', 64) },
        ["memory_delete_context"] = new() { ["context"] = "ctx" },
        ["memory_ingest_file"] = new() { ["path"] = Path.Combine(Path.GetTempPath(), "refusal.md") },
        ["memory_ingest_directory"] = new() { ["path"] = Path.GetTempPath() },
        ["memory_embed_pending"] = new(),
        ["memory_performance"] = new(),
        ["memory_promotion_list"] = new(),
        ["memory_promotion_discard"] = new(),
        ["memory_record_followthrough"] = new() { ["correlationId"] = "corr", ["filePath"] = "a.md" },
        ["memory_record_grade"] = new() { ["correlationId"] = "corr", ["grade"] = 1 },
        ["memory_share"] = new() { ["hash"] = new string('a', 64) },
        ["memory_share_extract"] = new(),
        ["memory_sweep"] = new(),
        ["memory_set_ttl"] = new() { ["hash"] = new string('a', 64), ["ttlDays"] = 1 },
        ["memory_sync"] = new(),
        ["memory_watch_add"] = new() { ["path"] = Path.GetTempPath() },
        ["memory_watch_status"] = new(),
        ["memory_watch_remove"] = new() { ["path"] = Path.GetTempPath() },
        ["memory_workspace_begin"] = new(),
        ["memory_workspace_status"] = new() { ["workspaceId"] = "ws-1" },
        ["memory_workspace_consolidate"] = new() { ["workspaceId"] = "ws-1", ["keep"] = Array.Empty<string>() },
        ["memory_workspace_discard"] = new() { ["workspaceId"] = "ws-1" }
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
        if (toolName == "memory_share_extract")
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
