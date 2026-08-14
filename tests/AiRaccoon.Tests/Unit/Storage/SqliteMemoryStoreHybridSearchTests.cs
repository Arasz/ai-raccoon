using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     FR-NM-4 (see docs/work/features-native-memory/native-memory.feature) hybrid search scenarios with a real vector modality: the store is configured
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
    private FakeEmbeddingEndpoint _openAi = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
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
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

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
    public async Task Search_VectorOnlyHit_SnippetFallsBackToTrimmedValue_WithEllipsis()
    {
        // A vector-only hit has no FTS5 snippet(); FR-NM-4 s1 (docs/work/features-native-memory/native-memory.feature)
        // still requires one, so the value is trimmed to ~200 chars, '…'-marked, keyed by hash.
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var longValue = string.Join(" ", Enumerable.Range(1, 40).Select(i =>
            $"sentence number {i} with enough prose to exceed the two hundred character window"));
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", longValue), TestContext.Current.CancellationToken);

        // No keyword overlap with the stored text, so only the vec modality retrieves it.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "fast canine"), TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Snippet.ShouldNotBeNullOrWhiteSpace();
        hit.Snippet.ShouldBe(SnippetFallback.From(longValue, hit.Hash));
    }

    [Fact]
    public async Task Search_VectorOnly_ReturnsExactlyLimitResults_WithCorrectSnippets_OutOfALargerCandidateSet()
    {
        // Laziness proof: N=8 vector-only candidates compete for a Limit=3 query; every returned
        // result must still carry the exact snippet SnippetFallback.From would produce eagerly.
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var entries = new List<MemoryEntry>();
        for (var i = 0; i < 8; i++)
        {
            var longValue = string.Join(" ", Enumerable.Range(1, 40).Select(n =>
                $"sentence number {n} candidate {i} with enough prose to exceed the two hundred character window"));
            entries.Add(await _store.WriteAsync(
                new MemoryWriteRequest("acme", longValue), TestContext.Current.CancellationToken));
        }

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "fast canine", SearchScope.Project, Limit: 3, MinScore: 0.0,
                RrfK: 60, FtsWeight: 0, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3, "3 survivors out of 8 candidates");
        foreach (var hit in results)
        {
            var source = entries.Single(e => e.Hash == hit.Hash);
            hit.Snippet.ShouldBe(SnippetFallback.From(source.Value, hit.Hash));
        }
    }

    [Fact]
    public async Task Search_FtsOnly_ReturnsExactlyLimitResults_WithNativeFtsSnippets_OutOfALargerCandidateSet()
    {
        // Laziness proof for the FTS modality (docs/plans/2026-08-08-search-knn-perf.md §WP7): N=8
        // FTS-only candidates compete for a Limit=3 query; every survivor carries the real FTS5
        // snippet() text, resolved only for the K survivors.
        var entries = new List<MemoryEntry>();
        for (var i = 0; i < 8; i++)
        {
            var longValue = $"zebra opens candidate {i}. " + string.Join(" ", Enumerable.Range(1, 40).Select(n =>
                $"sentence number {n} with enough prose to exceed the two hundred character window"));
            entries.Add(await _store.WriteAsync(
                new MemoryWriteRequest("acme", longValue), TestContext.Current.CancellationToken));
        }

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "zebra", SearchScope.Project, Limit: 3, MinScore: 0.0,
                RrfK: 60, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3, "3 survivors out of 8 FTS candidates");
        results.ShouldAllBe(hit => hit.Snippet.Contains("zebra", StringComparison.Ordinal),
            "survivors must carry the real FTS snippet() text, resolved lazily for ranking survivors only");
    }

    [Fact]
    public async Task Search_HashInBothFtsAndVectorLists_SnippetPrefersFtsNative_OverVectorFallback()
    {
        // Precedence (docs/plans/2026-08-08-search-knn-perf.md §WP7): ReciprocalRankFusion.Fuse fuses
        // the FTS list first, so a hash retrieved by both modalities keeps the FTS-native snippet()
        // text, not the vector modality's SnippetFallback trim.
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var longValue = "zebra opens the paragraph. " + string.Join(" ", Enumerable.Range(1, 40).Select(n =>
            $"sentence number {n} with enough prose to exceed the two hundred character window"));
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", longValue), TestContext.Current.CancellationToken);

        // A single embedded doc is always vector-rank-1 for any query; "zebra" also gives it an
        // FTS match, so it is retrieved by both modalities.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "zebra", SearchScope.Project, Limit: 5, MinScore: 0.0,
                RrfK: 60, FtsWeight: 1, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Snippet.ShouldContain("zebra");
        hit.Snippet.ShouldNotBe(SnippetFallback.From(longValue, hit.Hash));
    }

    [Fact]
    public async Task Search_VecOnlyQuery_RanksAscendingByDistance_AndNeverScoresWithDistance()
    {
        // vec_distance_cosine is a DISTANCE (0 = identical), so the vec list ranks ascending and
        // the fused Ranking stays an RRF score in 0..1 (top exactly 1.0), never the raw distance.
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var identical = await _store.AddContentAsync("acme", "a.md",
            "semantic memory retrieval system", ContextNaming.ProjectContext("acme"),
            cancellationToken: TestContext.Current.CancellationToken);
        var unrelated = await _store.AddContentAsync("acme", "b.md",
            "raspberry cheesecake recipe", ContextNaming.ProjectContext("acme"),
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "semantic memory retrieval system", SearchScope.Project,
                Limit: 10, MinScore: 0.0, RrfK: 60, FtsWeight: 0, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        // distance 0 (identical text) must rank first; the distance value never becomes a score.
        results.Select(r => r.Hash).ShouldBe([identical.Entry.Hash, unrelated.Entry.Hash]);
        results[0].Ranking.ShouldBe(1.0);
        results.ShouldAllBe(r => r.Ranking >= 0.0 && r.Ranking <= 1.0);
    }

    [Fact]
    public async Task Search_CandidateWindow_RescuesOverlapCandidateBeyondThePerModalityLimit()
    {
        // Per-modality candidate window K = max(limit*3, 100): a doc ranked second in both modalities
        // survives fusion for a limit-1 query even though neither per-modality top-1 is that doc.
        var a = await _store.AddContentAsync("acme", "a.md",
            "the quick brown fox jumps over the lazy dog", ContextNaming.ProjectContext("acme"),
            cancellationToken: TestContext.Current.CancellationToken);

        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var c = await _store.AddContentAsync("acme", "c.md",
            "quantum entanglement teleportation protocols", ContextNaming.ProjectContext("acme"),
            cancellationToken: TestContext.Current.CancellationToken);
        var x = await _store.AddContentAsync("acme", "x.md",
            "lazy dog sleeps on the warm rug", ContextNaming.ProjectContext("acme"),
            cancellationToken: TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "the quick brown fox jumps over the lazy dog", SearchScope.Project,
                Limit: 1, MinScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        // x is rank 2 in both lists: with K = max(1*3, 100) = 100 both lists carry it and
        // 1/62 + 1/62 beats the per-modality rank-1 docs' 1/61 each; a limit-1 query returns it.
        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(x.Entry.Hash);
        hit.Hash.ShouldNotBe(a.Entry.Hash);
        hit.Hash.ShouldNotBe(c.Entry.Hash);
    }

    [Fact]
    public async Task Search_FusionWeights_FlipTheWinnerBetweenKeywordAndVectorFavoured()
    {
        // keywordFavoured is FTS-only (written before the engine exists); vectorFavoured is
        // vector-only (written after). The RRF winner then depends purely on the configured
        // weights (FR-NM-4 s3; docs/work/features-native-memory/native-memory.feature).
        var keywordFavoured = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "api contract design"),
            TestContext.Current.CancellationToken);

        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", _openAi.BaseUrl, TestContext.Current.CancellationToken);

        var vectorFavoured = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "tax legislation for the 2026 fiscal year"),
            TestContext.Current.CancellationToken);

        // weights (2,1): the keyword list's rank-1 result scores 2/(k+1) > 1/(k+1).
        var keywordFirst = await _store.SearchAsync(
            new SearchQuery("acme", "api contract design", Limit: 10, MinScore: 0.0,
                RrfK: 60, FtsWeight: 2, VectorWeight: 1),
            TestContext.Current.CancellationToken);

        keywordFirst.Select(r => r.Hash).ShouldBe([keywordFavoured.Hash, vectorFavoured.Hash]);

        // weights (1,2): the vector list's rank-1 result scores 2/(k+1) > 1/(k+1).
        var vectorFirst = await _store.SearchAsync(
            new SearchQuery("acme", "api contract design", Limit: 10, MinScore: 0.0,
                RrfK: 60, FtsWeight: 1, VectorWeight: 2),
            TestContext.Current.CancellationToken);

        vectorFirst.Select(r => r.Hash).ShouldBe([vectorFavoured.Hash, keywordFavoured.Hash]);
    }

    [Fact]
    public async Task Search_ScopeShared_ExcludesProjectOnlyFacts()
    {
        // FR-NM-4 s4 (see docs/work/features-native-memory/native-memory.feature): scope selection is unchanged — a project-only fact is invisible to a
        // shared-scope search, while scope all still finds it.
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "project only fact about container registries"),
            TestContext.Current.CancellationToken);

        (await _store.SearchAsync(
                new SearchQuery("acme", "container", SearchScope.Shared),
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();

        (await _store.SearchAsync(
                new SearchQuery("acme", "container"),
                TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe([entry.Hash]);
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class StubChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
