using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Embedding;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Ingest chunking respects the embedding engine's token window (docs/work/2026-08-03-native-memory-plan.md
///     §8); bounds, not sizes, are pinned by FR-NM-10 (docs/work/features-native-memory/native-memory.feature).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreChunkingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);
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
        await AllowIngestScopeAsync(_dataRoot);

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

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("airaccoon-store-tests");

    /// <summary>Ingest is deny-by-default; these tests exercise chunking, not containment.</summary>
    private Task AllowIngestScopeAsync(string path) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([path]),
            TestContext.Current.CancellationToken);
}
