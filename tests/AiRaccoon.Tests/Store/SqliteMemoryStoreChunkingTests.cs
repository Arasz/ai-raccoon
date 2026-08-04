using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Embedding;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

/// <summary>
///     P6b plan §8 ADOPT (4): ingest chunking must respect the embedding engine's token
///     window — the old 512-token default exceeded the bundled all-MiniLM-L6-v2's 256-token
///     context, diluting embeddings via truncation. Chunk bounds are pinned by FR-NM-10
///     (bounds, not sizes), so the defaults may change.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreChunkingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private SqliteMemoryStore _store = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => { });
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new EmbeddingService());
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", _openAi.BaseUrl,
            "test-key-123", TestContext.Current.CancellationToken);
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
    public async Task IngestFile_ChunksNeverExceedTheEngineContextWindow()
    {
        var file = Path.Combine(_dataRoot, "long-note.md");
        await File.WriteAllTextAsync(file, BuildLongNote(), TestContext.Current.CancellationToken);

        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBe(1);
        var entries = await _store.ListContextAsync("acme", ContextNaming.ProjectContext("acme"),
            TestContext.Current.CancellationToken);
        entries.Count.ShouldBeGreaterThan(1);

        // The engine window (openai: 8191) bounds the chunk size; the default 256-token cap
        // applies on top, so no chunk exceeds the bundled model's 256-token window either.
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        entries.ShouldAllBe(entry => tokenizer.CountTokens(entry.Value) <= 256);
    }

    private static string BuildLongNote() =>
        string.Join(
            "\n",
            Enumerable.Range(1, 60).Select(i =>
                $"## Section {i}\n\nThis is paragraph {i} with enough prose to clearly exceed the token budget for a single chunk."));

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-chunking-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
