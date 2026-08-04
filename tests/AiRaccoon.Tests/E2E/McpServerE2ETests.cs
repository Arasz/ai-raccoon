using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Tests.Unit.Embedding;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Full-stack tests over the real HTTP MCP server (WebApplicationFactory + MCP client):
///     the tools, the managed store and the JSON-RPC transport all run together. No sqliteai
///     native extensions are required since P1 (the bank is our own memory.db). Embedding
///     round-trips run against the bundled ONNX model (local) and an in-process fake
///     OpenAI-compatible endpoint (openai).
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerE2ETests : IAsyncLifetime
{
    private McpClient _client = null!;
    private McpServerFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory();
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _factory?.Dispose();
        await _openAi.DisposeAsync();
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
        var begin = await CallAsync("memory_workspace_begin", ("projectId", "acme"));
        var wsId = JsonDocument.Parse(Text(begin)).RootElement.GetProperty("workspaceId").GetString()!;

        await CallAsync("memory_write",
            ("projectId", "acme"),
            ("content", "workspace secret"),
            ("workspaceId", wsId));

        // The write landed in the workspace outbox...
        var status = await CallAsync("memory_workspace_status",
            ("projectId", "acme"), ("workspaceId", wsId));
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
            null,
            null,
            CancellationToken.None);
        result.IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Embeddings_WithoutConfiguration_WritesStayDeferred_AndKeywordSearchStillWorks()
    {
        // FR-NM-3 s4: no engine configured → the write lands pending, memory_embed_pending
        // reports zero processed, and the interim FTS5 search still finds the row by keyword.
        await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "semantic e2e fact"));

        var embed = await CallAsync("memory_embed_pending", ("projectId", "acme"));
        var embedText = Text(embed);
        embedText.ShouldContain("\"processed\":0");
        embedText.ShouldContain("\"pending\":1");

        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"pending\":1");

        var search = await CallAsync("memory_search",
            ("projectId", "acme"), ("query", "semantic e2e"), ("scope", "project"));
        Text(search).ShouldContain("semantic e2e fact");
    }

    [Fact]
    public async Task Embeddings_ConfigureLocal_BundledEngine_EmbedsWritesEndToEnd()
    {
        // FR-NM-3 s1: provider local embeds in-process with the bundled model — no sidecar,
        // no download, no server process.
        var configure = await CallAsync("memory_configure",
            ("projectId", "acme"), ("provider", "local"));
        Text(configure).ShouldContain("\"engine\":\"local:bundled\"");

        await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "locally embedded e2e fact"));

        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"pending\":0");
    }

    [Fact]
    public async Task Embeddings_ConfigureOpenAi_RoutesThroughTheConfiguredEndpoint()
    {
        // FR-NM-3 s3: any OpenAI-compatible baseUrl replaces the default engine.
        var configure = await CallAsync("memory_configure",
            ("projectId", "acme"),
            ("provider", "openai"),
            ("baseUrl", _openAi.BaseUrl),
            ("model", "nomic-embed-text"),
            ("apiKey", "test-key-123"));
        Text(configure).ShouldContain($"\"engine\":\"openai:nomic-embed-text@{_openAi.BaseUrl}\"");

        await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "routed e2e fact"));

        var request = _openAi.Requests.ShouldHaveSingleItem();
        request.Model.ShouldBe("nomic-embed-text");
        request.Inputs.ShouldBe(["routed e2e fact"]);

        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"pending\":0");
    }

    [Fact]
    public async Task Embeddings_EmbedPending_ProcessesTheQueueAfterConfiguration()
    {
        // FR-NM-3 s5: deferred writes embed once an engine exists.
        await CallAsync("memory_write",
            ("projectId", "acme"), ("content", "queued e2e fact"));

        await CallAsync("memory_configure", ("projectId", "acme"), ("provider", "local"));

        var embed = await CallAsync("memory_embed_pending", ("projectId", "acme"));
        var embedText = Text(embed);
        embedText.ShouldContain("\"processed\":1");
        embedText.ShouldContain("\"pending\":0");

        var stats = await CallAsync("memory_stats", ("projectId", "acme"));
        Text(stats).ShouldContain("\"pending\":0");
    }

    private async Task<CallToolResult> CallAsync(string tool, params (string Key, object? Value)[] arguments)
    {
        var dict = arguments.ToDictionary(a => a.Key, a => a.Value);
        return await _client.CallToolAsync(tool, dict, null, null, CancellationToken.None);
    }

    private static string Text(CallToolResult result)
    {
        // isError is absent (null) on success in the MCP protocol — only true on failures.
        result.IsError.ShouldNotBe(true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return text;
    }
}
