using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Embedding;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Store-level FR-NM-3 scenarios (pluggable embeddings; bundled in-process ONNX default):
///     synchronous embed on write when configured, deferred writes without configuration,
///     embed_pending processing, and full re-embed on engine change — against the real local
///     engine and a fake OpenAI-compatible endpoint.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingFeatureTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new EmbeddingService());
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    [Fact]
    public async Task Embedding_ConfigureLocal_EmbedsWritesSynchronouslyWithoutASidecar()
    {
        var config = await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);

        config.Engine.ShouldBe("local:bundled");

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "locally embedded project fact"),
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        row.Embedding.ShouldNotBeNull();
        EmbeddingBlob.ToFloats(row.Embedding).Length.ShouldBe(384);
        (await CountVecRowsAsync()).ShouldBe(1);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task Embedding_ConfigureLocal_CustomModelPath_OverridesTheBundledModel()
    {
        var custom = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model",
            Guid.NewGuid().ToString("N"), BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.Copy(BundledModel.ResolveModelPath(), custom);
        try
        {
            var config = await _store.ConfigureEmbeddingAsync("acme", "local", custom, null, null,
                TestContext.Current.CancellationToken);

            config.Engine.ShouldBe($"local:{custom}");

            var entry = await _store.WriteAsync(
                new MemoryWriteRequest("acme", "embedded with a custom model path"),
                TestContext.Current.CancellationToken);

            var row = await ReadRowAsync(entry.Hash);
            row.EmbedState.ShouldBe("embedded");
            row.Embedding.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(custom)!, true);
        }
    }

    [Fact]
    public async Task Embedding_ConfigureOpenAi_RoutesWritesThroughTheBaseUrl()
    {
        await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", _openAi.BaseUrl, "test-key-123",
            TestContext.Current.CancellationToken);

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "routed through an openai compatible endpoint"),
            TestContext.Current.CancellationToken);

        var request = _openAi.Requests.ShouldHaveSingleItem();
        request.Model.ShouldBe("nomic-embed-text");
        request.Inputs.ShouldBe(["routed through an openai compatible endpoint"]);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        var stored = EmbeddingBlob.ToFloats(row.Embedding);
        stored[0].ShouldBe(FakeEmbeddingEndpoint.VectorFor("routed through an openai compatible endpoint")[0]);
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_WithoutConfiguration_WritesStayDeferredAndStatsReportPending()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "deferred until an engine is configured"),
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("pending");
        row.Embedding.ShouldBeNull();
        (await CountVecRowsAsync()).ShouldBe(0);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).PendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_EmbedPending_ProcessesTheDeferredQueueAfterConfiguration()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "queued before configuration"),
            TestContext.Current.CancellationToken);

        // Configuring the engine does not vacuum the pending queue — embed_pending owns it.
        await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);

        var result = await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        result.Processed.ShouldBe(1);
        result.Pending.ShouldBe(0);
        var row = await ReadRowAsync(entry.Hash);
        row.EmbedState.ShouldBe("embedded");
        row.Embedding.ShouldNotBeNull();
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_EngineChange_ReembedsPreviouslyEmbeddedEntriesWithTheNewEngine()
    {
        var first = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact one for engine switch"),
            TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact two for engine switch"),
            TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);
        await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        var localVectors = (await ReadRowAsync(first.Hash), await ReadRowAsync(second.Hash));
        localVectors.Item1.EmbedState.ShouldBe("embedded");

        // New engine: any OpenAI-compatible provider (the fake endpoint).
        await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", _openAi.BaseUrl, "test-key-123",
            TestContext.Current.CancellationToken);

        var reembedded = (await ReadRowAsync(first.Hash), await ReadRowAsync(second.Hash));
        reembedded.Item1.EmbedState.ShouldBe("embedded");
        reembedded.Item2.EmbedState.ShouldBe("embedded");
        EmbeddingBlob.ToFloats(reembedded.Item1.Embedding)[0].ShouldBe(
            FakeEmbeddingEndpoint.VectorFor("fact one for engine switch", 0)[0]);
        EmbeddingBlob.ToFloats(reembedded.Item2.Embedding)[0].ShouldBe(
            FakeEmbeddingEndpoint.VectorFor("fact two for engine switch", 1)[0]);
        (await CountVecRowsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Embedding_Configure_SameEngineDoesNotReembed()
    {
        await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "stable fact"),
            TestContext.Current.CancellationToken);

        await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);

        // Re-configuring the identical engine must not invalidate or touch existing rows.
        var row = await ReadRowAsync((await _store.ListContextAsync("acme", "project:acme",
            TestContext.Current.CancellationToken)).Single().Hash);
        row.EmbedState.ShouldBe("embedded");
        (await CountVecRowsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Embedding_Delete_RemovesTheVectorRowToo()
    {
        await _store.ConfigureEmbeddingAsync("acme", "local", null, null, null,
            TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "fact with a vector"),
            TestContext.Current.CancellationToken);
        (await CountVecRowsAsync()).ShouldBe(1);

        await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        (await CountVecRowsAsync()).ShouldBe(0);
    }

    private async Task<EntryRow> ReadRowAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<EntryRow>(
            new CommandDefinition(
                "SELECT embed_state AS EmbedState, embedding AS Embedding FROM entries WHERE hash = @hash",
                new { hash }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT count(*) FROM vec_entries",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("ai-raccoon-tests");

    private sealed class EntryRow
    {
        public string EmbedState { get; set; } = "";

        public byte[]? Embedding { get; set; }
    }
}
