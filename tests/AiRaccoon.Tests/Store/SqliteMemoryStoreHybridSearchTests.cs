using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Embedding;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

/// <summary>
///     FR-NM-4 hybrid search scenarios with a real vector modality: the store is configured
///     with the fake OpenAI-compatible endpoint so rows embed (deterministic vectors) and the
///     vec0 list is populated, exercising the RRF fusion across FTS5 and vec0.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreHybridSearchTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => { });
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new StubChunker(),
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
    public async Task Search_WithConfiguredEngine_ReturnsVectorOnlyHit_WhenKeywordHasNoMatch()
    {
        await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", _openAi.BaseUrl,
            "test-key-123", TestContext.Current.CancellationToken);

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "the quick brown fox leaps over the lazy dog"),
            TestContext.Current.CancellationToken);

        // No keyword overlap with the stored text, so only the vec modality can retrieve it.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "fast canine"),
            TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Path.ShouldBe(entry.Path);
        hit.Snippet.ShouldNotBeNullOrWhiteSpace();
        hit.Ranking.ShouldBeInRange(0.0, 1.0);
    }

    [Fact]
    public async Task Search_FusionWeights_FlipTheWinnerBetweenKeywordAndVectorFavoured()
    {
        // d1 is written before any engine exists -> FTS-indexed, never embedded (pending), so
        // it can only enter the keyword list. d2 is written after configuring the fake engine
        // -> embedded, so it only enters the vector list. The RRF winner then depends purely
        // on the configured weights (FR-NM-4 s3: weights 2:1 vs 1:2).
        var keywordFavoured = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "api contract design"),
            TestContext.Current.CancellationToken);

        await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", _openAi.BaseUrl,
            "test-key-123", TestContext.Current.CancellationToken);

        var vectorFavoured = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "tax legislation for the 2026 fiscal year"),
            TestContext.Current.CancellationToken);

        // weights (2,1): the keyword list's rank-1 result scores 2/(k+1) > 1/(k+1).
        var keywordFirst = await _store.SearchAsync(
            new SearchQuery("acme", "api contract design", limit: 10, minScore: 0.0,
                rrfK: 60, ftsWeight: 2, vectorWeight: 1),
            TestContext.Current.CancellationToken);

        keywordFirst.Select(r => r.Hash).ShouldBe([keywordFavoured.Hash, vectorFavoured.Hash]);

        // weights (1,2): the vector list's rank-1 result scores 2/(k+1) > 1/(k+1).
        var vectorFirst = await _store.SearchAsync(
            new SearchQuery("acme", "api contract design", limit: 10, minScore: 0.0,
                rrfK: 60, ftsWeight: 1, vectorWeight: 2),
            TestContext.Current.CancellationToken);

        vectorFirst.Select(r => r.Hash).ShouldBe([vectorFavoured.Hash, keywordFavoured.Hash]);
    }

    [Fact]
    public async Task Search_ScopeShared_ExcludesProjectOnlyFacts()
    {
        // FR-NM-4 s4: scope selection is unchanged — a project-only fact is invisible to a
        // shared-scope search, while scope all still finds it.
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "project only fact about container registries"),
            TestContext.Current.CancellationToken);

        (await _store.SearchAsync(
                new SearchQuery("acme", "container", SearchScope.Shared),
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();

        (await _store.SearchAsync(
                new SearchQuery("acme", "container", SearchScope.All),
                TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe([entry.Hash]);
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-hybrid-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}