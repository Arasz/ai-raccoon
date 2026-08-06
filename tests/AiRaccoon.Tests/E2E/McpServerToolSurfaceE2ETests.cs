using System.Text.Json;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.Unit.Embedding;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Tool-surface parity over the real HTTP MCP server: tools/list must surface all 20 tools,
///     and every tool not already round-tripped by <see cref="McpServerE2ETests"/> answers a
///     minimal call over the wire.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerToolSurfaceE2ETests : IAsyncLifetime
{
    private const string ProjectId = "surface-test";

    private static readonly string[] ExpectedToolNames =
    [
        // 17 memory_* tools
        "memory_write",
        "memory_search",
        "memory_list",
        "memory_stats",
        "memory_share",
        "memory_share_extract",
        "memory_delete",
        "memory_delete_context",
        "memory_ingest_file",
        "memory_ingest_directory",
        "memory_embed_pending",
        "memory_workspace_begin",
        "memory_workspace_status",
        "memory_workspace_consolidate",
        "memory_workspace_discard",
        "memory_sweep",
        "memory_sync",
        // 3 watch_* tools
        "memory_watch_add",
        "memory_watch_status",
        "memory_watch_remove"
    ];

    private McpClient _client = null!;
    private McpServerFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
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
    public async Task ToolsList_SurfacesAllTwentyTools()
    {
        var tools = await _client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToArray();

        names.OrderBy(n => n).ShouldBe(ExpectedToolNames.OrderBy(n => n));
    }

    [Fact]
    public async Task UncoveredTools_RoundTripOverTheWire()
    {
        // memory_list: answers on a fresh project.
        var list = await CallAsync("memory_list", ("projectId", ProjectId));
        Text(list).ShouldNotBeNullOrWhiteSpace();

        // memory_delete: write a row, then delete it by its hash.
        var write = await CallAsync("memory_write", ("projectId", ProjectId), ("content", "surface parity fact"));
        var hash = JsonDocument.Parse(Text(write)).RootElement.GetProperty("hash").GetString();
        hash.ShouldNotBeNullOrWhiteSpace();
        var delete = await CallAsync("memory_delete", ("projectId", ProjectId), ("hash", hash));
        JsonDocument.Parse(Text(delete)).RootElement.GetProperty("deleted").GetInt32().ShouldBe(1);

        // memory_delete_context: write a row, then purge its context.
        await CallAsync("memory_write", ("projectId", ProjectId), ("content", "context purge me"));
        var deleteContext = await CallAsync("memory_delete_context", ("projectId", ProjectId), ("context", $"project:{ProjectId}"));
        JsonDocument.Parse(Text(deleteContext)).RootElement.GetProperty("deleted").GetInt32().ShouldBe(1);

        // memory_ingest_file: index one temp file.
        var file = Path.Combine(Path.GetTempPath(), $"ai-raccoon-surface-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "ingested surface fact");
        try
        {
            var ingest = await CallAsync("memory_ingest_file", ("projectId", ProjectId), ("path", file));
            JsonDocument.Parse(Text(ingest)).RootElement.GetProperty("indexed").GetInt32().ShouldBe(1);
        }
        finally
        {
            File.Delete(file);
        }

        // memory_ingest_directory: index one temp directory.
        var dir = Directory.CreateTempSubdirectory("ai-raccoon-surface-");
        File.WriteAllText(Path.Combine(dir.FullName, "note.md"), "directory surface fact");
        try
        {
            var scanned = await CallAsync("memory_ingest_directory", ("projectId", ProjectId), ("path", dir.FullName));
            JsonDocument.Parse(Text(scanned)).RootElement.GetProperty("scanned").GetInt32().ShouldBe(1);
        }
        finally
        {
            dir.Delete(true);
        }

        // memory_sweep: dry-run (default) reports the {candidates, deleted} shape.
        var sweep = await CallAsync("memory_sweep", ("projectId", ProjectId));
        using var sweepDoc = JsonDocument.Parse(Text(sweep));
        sweepDoc.RootElement.GetProperty("candidates").ValueKind.ShouldBe(JsonValueKind.Array);
        sweepDoc.RootElement.GetProperty("deleted").ValueKind.ShouldBe(JsonValueKind.Array);

        // memory_share_extract: propose mode returns {candidates, promotedHashes} without sharing.
        var extract = await CallAsync("memory_share_extract",
            ("projectIds", new[] { ProjectId }), ("mode", "propose"));
        using var extractDoc = JsonDocument.Parse(Text(extract));
        extractDoc.RootElement.GetProperty("candidates").ValueKind.ShouldBe(JsonValueKind.Array);
        extractDoc.RootElement.GetProperty("promotedHashes").ValueKind.ShouldBe(JsonValueKind.Array);

        // watch trio: enabled + scoped via settings, then add -> status -> remove.
        var watchDir = Directory.CreateTempSubdirectory("ai-raccoon-surface-watch-");
        try
        {
            await SeedWatchConfigAsync(watchDir.FullName);

            var add = await CallAsync("memory_watch_add", ("projectId", ProjectId), ("path", watchDir.FullName));
            var addText = Text(add);
            addText.ShouldContain($"\"path\":\"{watchDir.FullName}\"");

            var status = await CallAsync("memory_watch_status", ("projectId", ProjectId));
            using var statusDoc = JsonDocument.Parse(Text(status));
            var watches = statusDoc.RootElement.GetProperty("watches");
            var watch = watches.EnumerateArray()
                .First(w => w.GetProperty("path").GetString() == watchDir.FullName);
            var state = watch.GetProperty("state").GetString()?.ToLowerInvariant();
            state.ShouldBeOneOf("scanning", "healthy");

            var remove = await CallAsync("memory_watch_remove", ("projectId", ProjectId), ("path", watchDir.FullName));
            Text(remove).ShouldContain($"\"path\":\"{watchDir.FullName}\"");
        }
        finally
        {
            try
            {
                watchDir.Delete(true);
            }
            catch (IOException)
            {
                // Background scan may still hold a handle on a loaded machine; the
                // factory's temp root is removed at dispose anyway.
            }
        }

        // Clean up the dedicated project context.
        await CallAsync("memory_delete_context", ("projectId", ProjectId), ("context", $"project:{ProjectId}"));
    }

    private async Task SeedWatchConfigAsync(string tempDir)
    {
        var options = new InfrastructureOptions { DataRoot = _factory.DataRoot, Scope = InstallScope.User };
        var store = new SqliteMemoryStore(
            new SqliteConnectionFactory(options,
                new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                    [new EnvEncryptionKeyProvider()])),
            TimeProvider.System, new TokenizerChunker(), new EmbeddingService());
        await store.SetSettingAsync(WatchConfigKeys.EnabledProject(ProjectId), "true");
        await store.SetSettingAsync(WatchConfigKeys.ScopeProject(ProjectId), WatchConfigKeys.SerializeScope([tempDir]));
    }

    private async Task<CallToolResult> CallAsync(string tool, params (string Key, object? Value)[] arguments)
    {
        var dict = arguments.ToDictionary(a => a.Key, a => a.Value);
        return await _client.CallToolAsync(tool, dict, null, null, TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult result)
    {
        // isError is absent (null) on success in the MCP protocol — only true on failures.
        result.IsError.ShouldNotBe(true);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}
