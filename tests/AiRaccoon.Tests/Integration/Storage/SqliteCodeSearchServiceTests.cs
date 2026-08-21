using System.Globalization;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP5/WP6 — SqliteCodeSearchService: FTS5 + vec0 + RRF hybrid fusion, project scoping, ranking
///     normalization (hybrid-score-relative, mirroring memory's RRF), minRelativeScore, empty-corpus,
///     code_get's hash round-trip, and the degraded modes (unconfigured/unloadable engine). Rows are
///     seeded directly by SQL (no CodeIngestor in this file — WP3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteCodeSearchServiceTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-search-tests");
    private SqliteConnectionFactory _factory = null!;
    private FakeCodeEmbedder _embedder = null!;
    private SqliteCodeSearchService _service = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _embedder = new FakeCodeEmbedder();
        _service = new SqliteCodeSearchService(_factory, _embedder);
        // Opening the bank once runs MemorySchema.EnsureAsync, so the code corpus tables exist
        // before any test seeds rows into them.
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SearchAsync_MatchesSeededRows_AndCarriesPathAndLineRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Foo.cs", value: "sealed class QuokkaFinder { }",
            lineStart: 1, lineEnd: 3);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "QuokkaFinder", 20, 0.0),
            TestContext.Current.CancellationToken);

        var hit = results.Results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe("hash-1");
        hit.Path.ShouldBe("src/Foo.cs");
        hit.LineStart.ShouldBe(1);
        hit.LineEnd.ShouldBe(3);
        // Ranking on a single-hit result is not asserted here: max-normalization puts rank 1 at
        // 1.0 unconditionally, so that would be theatre (integration review small item 4) --
        // SearchAsync_RanksAStrongerMatchStrictlyAboveAWeakerOne below carries the real claim.
    }

    /// <summary>
    ///     Integration review small item 4: SearchAsync_MatchesSeededRows_AndCarriesPathAndLineRange's
    ///     Ranking.ShouldBe(1.0) was theatre — with a single seeded row, max-normalization puts rank 1
    ///     at 1.0 no matter how weak or broken the underlying bm25 ordering is, so the assertion could
    ///     never catch a ranking regression. This test seeds two rows with genuinely different match
    ///     strength (mirrors SearchAsync_MinRelativeScore_DropsTheWeakerHit's fixture) and asserts the
    ///     stronger match ranks strictly above the weaker one — a real ordering claim, not a tautology
    ///     of the normalization formula.
    /// </summary>
    [Fact]
    public async Task SearchAsync_RanksAStrongerMatchStrictlyAboveAWeakerOne()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/A.cs", value: "class DingoTracker DingoTracker DingoTracker { }",
            lineStart: 1, lineEnd: 1);
        await SeedAsync(id: 2, projectId: "acme", path: "src/B.cs", value: "class DingoTracker { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "DingoTracker", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.Count.ShouldBe(2);
        var strongest = results.Results[0];
        var weakest = results.Results[1];
        strongest.Hash.ShouldBe("hash-1", "the row repeating the term three times must rank ahead of the row with one occurrence");
        strongest.Ranking.ShouldBeGreaterThan(weakest.Ranking,
            "a genuinely weaker match must normalize below the top hit, not tie with it");
    }

    [Fact]
    public async Task SearchAsync_NeverLeaksAcrossProjects()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Program.cs", value: "class WombatRunner { }",
            lineStart: 1, lineEnd: 1);
        await SeedAsync(id: 2, projectId: "other", path: "src/Program.cs", value: "class WombatRunner { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "WombatRunner", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem().Hash.ShouldBe("hash-1");
    }

    [Fact]
    public async Task SearchAsync_MinRelativeScore_DropsTheWeakerHit()
    {
        // Two matches for the same term, seeded so rank 2's normalized score (61/62) sits below a
        // floor that rank 1 (1.0) always clears.
        await SeedAsync(id: 1, projectId: "acme", path: "src/A.cs", value: "class DingoTracker DingoTracker DingoTracker { }",
            lineStart: 1, lineEnd: 1);
        await SeedAsync(id: 2, projectId: "acme", path: "src/B.cs", value: "class DingoTracker { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "DingoTracker", 20, 0.99),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem("minRelativeScore=0.99 keeps only rank 1 (1.0); rank 2 normalizes below it");
    }

    [Fact]
    public async Task SearchAsync_EmptyCorpus_ReturnsEmptyResults_WithoutError()
    {
        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "anything", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_KnownHash_ReturnsFullSourceWithPathAndRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "sealed class NarwhalTusk { }",
            lineStart: 5, lineEnd: 9);

        var entry = await _service.GetAsync("acme", "hash-1", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry!.Value.ShouldBe("sealed class NarwhalTusk { }");
        entry.Path.ShouldBe("src/Bar.cs");
        entry.LineStart.ShouldBe(5);
        entry.LineEnd.ShouldBe(9);
    }

    [Fact]
    public async Task GetAsync_UnknownHash_ReturnsNull()
    {
        var entry = await _service.GetAsync("acme", "does-not-exist", TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_KnownHashInAnotherProject_ReturnsNull()
    {
        await SeedAsync(id: 1, projectId: "other", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);

        var entry = await _service.GetAsync("acme", "hash-1", TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
    }

    // ---- WP5: hybrid vector leg, weight-flip, relativeScore semantics, degraded modes ----

    [Fact]
    public async Task SearchAsync_VectorLegFindsAHitFtsNeverWouldMatch()
    {
        // FTS can never match this row (the query term appears nowhere in its text); only a
        // genuine vector leg can surface it.
        var vector = Repeat(1f);
        await SeedEmbeddedAsync(id: 1, projectId: "acme", path: "src/Only.cs", value: "totally unrelated content",
            lineStart: 1, lineEnd: 1, vector: vector);
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(vector));

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "SemanticQuery", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem().Hash.ShouldBe("hash-1",
            "FTS alone would never match unrelated text; only the vector leg can surface this hit");
    }

    [Fact]
    public async Task SearchAsync_VectorLeg_NeverLeaksAcrossProjects()
    {
        var vector = Repeat(1f);
        await SeedEmbeddedAsync(id: 1, projectId: "other", path: "src/Only.cs", value: "totally unrelated content",
            lineStart: 1, lineEnd: 1, vector: vector);
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(vector));

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "SemanticQuery", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldBeEmpty("vec_code's ctx = project_id partition must exclude another project's row");
    }

    [Fact]
    public async Task SearchAsync_WeightFlip_VectorFavoredVsFtsFavoredOrderingChanges()
    {
        // Row A: strong FTS match, weak/opposite vector match. Row B: weak FTS match, exact vector match.
        await SeedEmbeddedAsync(id: 1, projectId: "acme", path: "src/A.cs",
            value: "class HeronCatcher HeronCatcher HeronCatcher { }", lineStart: 1, lineEnd: 1, vector: Repeat(-1f));
        await SeedEmbeddedAsync(id: 2, projectId: "acme", path: "src/B.cs",
            value: "class HeronCatcher { }", lineStart: 1, lineEnd: 1, vector: Repeat(1f));
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(Repeat(1f)));
        var query = new CodeSearchQuery("acme", "HeronCatcher", 20, 0.0);

        await SetRetrievalWeightsAsync(ftsWeight: 100, vectorWeight: 1);
        var ftsFavored = await _service.SearchAsync(query, TestContext.Current.CancellationToken);

        await SetRetrievalWeightsAsync(ftsWeight: 1, vectorWeight: 100);
        var vectorFavored = await _service.SearchAsync(query, TestContext.Current.CancellationToken);

        ftsFavored.Results[0].Hash.ShouldBe("hash-1", "an FTS-weighted fusion must rank the stronger keyword match first");
        vectorFavored.Results[0].Hash.ShouldBe("hash-2", "a vector-weighted fusion must rank the exact semantic match first");
    }

    [Fact]
    public async Task SearchAsync_RelativeScore_ReflectsFusedRelevance_NotFtsRankAlone()
    {
        // row1: FTS-strong (term x3), never embedded -- absent from the vector leg entirely.
        await SeedAsync(id: 1, projectId: "acme", path: "src/A.cs",
            value: "class GriffinTracker GriffinTracker GriffinTracker { }", lineStart: 1, lineEnd: 1);
        // row2: FTS-weak (term once), but its embedding exactly matches the query vector.
        var vector = Repeat(1f);
        await SeedEmbeddedAsync(id: 2, projectId: "acme", path: "src/B.cs",
            value: "class GriffinTracker { }", lineStart: 1, lineEnd: 1, vector: vector);
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(vector));

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "GriffinTracker", 20, 0.0),
            TestContext.Current.CancellationToken);

        // Under the old FTS-only positional formula, row2 (FTS rank 2) would score exactly
        // (k+1)/(k+2) -- a fixed constant derived from its FTS RANK alone. The hybrid fusion
        // instead ranks it FIRST: a perfect vector match outweighs a merely-repeated keyword
        // match once both legs are genuinely fused -- something pure rank position can never see.
        results.Results[0].Hash.ShouldBe("hash-2",
            "a perfect vector match must be able to outrank a stronger keyword match once both legs are fused");
        results.Results[0].Ranking.ShouldBe(1.0);
    }

    [Fact]
    public async Task SearchAsync_StructureAlphaSetting_DoesNotMoveCodeRanking()
    {
        await SeedEmbeddedAsync(id: 1, projectId: "acme", path: "src/A.cs",
            value: "class KestrelSpotter KestrelSpotter { }", lineStart: 1, lineEnd: 1, vector: Repeat(-1f));
        await SeedEmbeddedAsync(id: 2, projectId: "acme", path: "src/B.cs",
            value: "class KestrelSpotter { }", lineStart: 1, lineEnd: 1, vector: Repeat(1f));
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(Repeat(1f)));
        var query = new CodeSearchQuery("acme", "KestrelSpotter", 20, 0.0);

        await SetSettingAsync("retrieval.structureAlpha", "0.1");
        var baseline = await _service.SearchAsync(query, TestContext.Current.CancellationToken);

        await SetSettingAsync("retrieval.structureAlpha", "0.99");
        var afterAlphaChange = await _service.SearchAsync(query, TestContext.Current.CancellationToken);

        afterAlphaChange.Results.Select(r => (r.Hash, r.Ranking)).ShouldBe(
            baseline.Results.Select(r => (r.Hash, r.Ranking)),
            "code has no structure modality (§3.1) -- retrieval.structureAlpha must never move code ranking");
    }

    [Fact]
    public async Task SearchAsync_NoCodeEngineConfigured_DegradesToFtsOnly_WithWarning()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/A.cs", value: "class QuokkaFinder { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "QuokkaFinder", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem();
        results.Warning.ShouldBe(CodeSearchWarnings.EngineNotConfigured);
    }

    [Fact]
    public async Task SearchAsync_QueryTrimmedByEmbedder_CarriesCodeBudgetWarning()
    {
        _embedder.QueryVectorToReturn = new QueryVector(EmbeddingBlob.ToBytes(Repeat(1f))) { Trimmed = true };

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "anything", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Warning.ShouldBe(CodeSearchWarnings.QueryTrimmedToCodeWindow);
    }

    [Fact]
    public async Task SearchAsync_ConfiguredButUnloadable_ThrowsActionableError()
    {
        _embedder.ThrowOnEmbedQuery = new CodeEngineUnloadableException("/models/broken",
            new InvalidOperationException("model.onnx is not a valid ONNX file"));

        var ex = await Should.ThrowAsync<CodeEngineUnloadableException>(() => _service.SearchAsync(
            new CodeSearchQuery("acme", "anything", 20, 0.0), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("/models/broken");
    }

    private static float[] Repeat(float value) => Enumerable.Repeat(value, 768).ToArray();

    private async Task SetRetrievalWeightsAsync(int ftsWeight, int vectorWeight)
    {
        await SetSettingAsync("retrieval.ftsWeight", ftsWeight.ToString(CultureInfo.InvariantCulture));
        await SetSettingAsync("retrieval.vectorWeight", vectorWeight.ToString(CultureInfo.InvariantCulture));
    }

    private async Task SetSettingAsync(string key, string value)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Inserts pending, then flips to embedded via UPDATE -- vec_code_au only fires on UPDATE OF embed_state, never on INSERT.</summary>
    private async Task SeedEmbeddedAsync(long id, string projectId, string path, string value, int lineStart,
        int lineEnd, float[] vector)
    {
        await SeedAsync(id, projectId, path, value, lineStart, lineEnd);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = @id",
            new { id, embedding = EmbeddingBlob.ToBytes(vector) }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedAsync(long id, string projectId, string path, string value, int lineStart, int lineEnd)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, @lineStart, @lineEnd, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path, value, lineStart, lineEnd, projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
