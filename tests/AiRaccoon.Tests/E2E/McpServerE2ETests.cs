using System.Text.Json;
using AiRaccoon.Tests.E2E;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
/// Full-stack tests over the real HTTP MCP server (WebApplicationFactory + MCP client):
/// the tools, the store, the native sqlite-memory extension and the JSON-RPC transport
/// all run together. Requires the host RID's native extensions to be provisioned
/// (~/.ai-raccoon/extensions/&lt;rid&gt;) — otherwise they skip honestly, like the store
/// integration tests. See the E2E collection: env mutation forces serial execution.
/// Assertions use stats/status/list (no embeddings required) except the dedicated
/// embeddings test, which needs AIRACCOON_TEST_GGUF.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerE2ETests : IAsyncLifetime
{
    private McpServerFactory _factory = null!;
    private McpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new McpServerFactory();
        if (!_factory.HasNativeExtensions)
        {
            Assert.Skip("native extensions not provisioned for this host RID; skipping E2E tests");
        }

        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _factory?.Dispose();
    }

    [Fact]
    public async Task Save_WriteThenStats_ReportsCommittedEntry()
    {
        var write = await CallAsync("memory_write", ("projectId", "acme"), ("content", "E2E durable fact"));

        var writeText = Text(write);
        writeText.ShouldContain("\"context\":\"project:acme\"");
        writeText.ShouldContain("\"hash\"");

        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        var statsText = Text(stats);
        statsText.ShouldContain("\"entries\":1");
        statsText.ShouldContain("project:acme");
    }

    [Fact]
    public async Task Isolation_WorkspaceWrite_IsNotVisibleInProjectScope()
    {
        await CallAsync("memory_workspace_begin", ("projectId", "acme"));
        await CallAsync("memory_write",
            ("projectId", "acme"),
            ("content", "workspace secret"),
            ("workspaceId", "ws-iso"));

        // The write landed in the workspace outbox...
        var status = await CallAsync("memory_workspace_status",
            ("projectId", "acme"), ("workspaceId", "ws-iso"));
        Text(status).ShouldContain("workspace secret");

        // ...and NOT in the project's committed context: stats counts only committed
        // project entries, so an un-consolidated workspace write must not appear.
        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"entries\":0");
    }

    [Fact]
    public async Task Worktree_BeginStatusConsolidate_EndsWithCommittedEntry()
    {
        var begin = await CallAsync("memory_workspace_begin",
            ("projectId", "acme"), ("name", "feature-x"));
        var workspaceId = JsonDocument.Parse(Text(begin)).RootElement.GetProperty("workspaceId").GetString();
        workspaceId.ShouldNotBeNullOrWhiteSpace();

        await CallAsync("memory_write",
            ("projectId", "acme"),
            ("content", "durable from worktree"),
            ("workspaceId", workspaceId));

        var status = await CallAsync("memory_workspace_status",
            ("projectId", "acme"), ("workspaceId", workspaceId));
        Text(status).ShouldContain("durable from worktree");

        var consolidate = await CallAsync("memory_workspace_consolidate",
            ("projectId", "acme"), ("workspaceId", workspaceId), ("keep", new[] { "all" }));
        Text(consolidate).ShouldContain("\"promoted\":1");

        // The promoted fact is now in the project context and the outbox is empty.
        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"entries\":1");
        var statusAfter = await CallAsync("memory_workspace_status",
            ("projectId", "acme"), ("workspaceId", workspaceId));
        Text(statusAfter).ShouldContain("\"count\":0");
    }

    [Fact]
    public async Task Worktree_Discard_RemovesTheOutbox()
    {
        var begin = await CallAsync("memory_workspace_begin", ("projectId", "acme"));
        var workspaceId = JsonDocument.Parse(Text(begin)).RootElement.GetProperty("workspaceId").GetString()!;

        await CallAsync("memory_write",
            ("projectId", "acme"),
            ("content", "to be discarded"),
            ("workspaceId", workspaceId));

        var discard = await CallAsync("memory_workspace_discard",
            ("projectId", "acme"), ("workspaceId", workspaceId));
        Text(discard).ShouldContain("\"deleted\":1");

        var status = await CallAsync("memory_workspace_status",
            ("projectId", "acme"), ("workspaceId", workspaceId));
        Text(status).ShouldContain("\"count\":0");

        // Nothing leaked into the project context.
        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"entries\":0");
    }

    [Fact]
    public async Task MoveToShared_PromotesEntry_AndAppearsInSharedScope()
    {
        var write = await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "cross project convention"));
        var hash = JsonDocument.Parse(Text(write)).RootElement.GetProperty("hash").GetString()!;

        var share = await CallAsync("memory_share", ("projectId", "acme"), ("hash", hash));
        Text(share).ShouldContain("shared");

        // The shared context now holds the promoted row; the project row remains.
        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        var statsText = Text(stats);
        statsText.ShouldContain("\"entries\":1");
        statsText.ShouldContain("shared");
    }

    [Fact]
    public async Task Sync_WithoutCredentials_ReportsSyncNotConfigured()
    {
        // No AIRACCOON_SQLITECLOUD_* env in the test process: the sync tool must surface
        // the not-configured error as an MCP error rather than crash the server.
        var result = await _client.CallToolAsync(
            "memory_sync",
            new Dictionary<string, object?> { ["projectId"] = "acme" },
            progress: null,
            options: null,
            CancellationToken.None);
        result.IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Embeddings_ConfigureEmbedThenSearch_WithConfiguredModel()
    {
        var model = Environment.GetEnvironmentVariable("AIRACCOON_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(model))
        {
            Assert.Skip("AIRACCOON_TEST_GGUF not set; skipping the real-embedding E2E round-trip");
        }

        await CallAsync("memory_configure",
            ("projectId", "acme"), ("provider", "local"), ("model", model));
        await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "semantic e2e fact"));

        var embed = await CallAsync("memory_embed_pending", ("projectId", "acme"));
        Text(embed).ShouldContain("\"processed\"");

        var search = await CallAsync("memory_search",
            ("projectId", "acme"), ("query", "semantic e2e"), ("scope", "project"));
        Text(search).ShouldContain("semantic e2e fact");
    }

    private async Task<CallToolResult> CallAsync(string tool, params (string Key, object? Value)[] arguments)
    {
        var dict = arguments.ToDictionary(a => a.Key, a => a.Value);
        return await _client.CallToolAsync(tool, dict, progress: null, options: null, CancellationToken.None);
    }

    private static string Text(CallToolResult result)
    {
        // isError is absent (null) on success in the MCP protocol — only true on failures.
        result.IsError.ShouldNotBe(true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return text;
    }
}
