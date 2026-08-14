using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     The acceptance test for WP4 (docs/plans/2026-08-14-code-quality-improvement-plan.md): the
///     regenerated corpus (tests/AiRaccoon.Tests/Resources/jsaa-memory.db) is the first committed
///     gate fixture to carry real structure vectors (docs/adr/0004-dual-vector-structure-signal.md),
///     so it is the first fixture that can prove <see cref="StructureFusion" /> is load-bearing —
///     deliberately breaking it must make a gate here go red.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class StructureFusionGateTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant";

    /// <summary>
    ///     A deliberately generic query: its content-embedding similarity to any one candidate chunk
    ///     is weak and roughly tied across many ADR chunks, so which chunk wins the vector-only
    ///     ranking is decided by the structure leg matching the query against the "Decision" heading —
    ///     exactly the dual-vector signal ADR-0004 adds. Measured against the regenerated corpus
    ///     (docs/reviews/2026-08-14-moe-codebase-review.md RAG-F2): with real fusion this chunk (the
    ///     Decision section of ADR-0068) ranks #1 at the fused-score ceiling (1.0, the top result is
    ///     always normalized to 1.0 — see <see cref="StructureFusion.Rank" />); ADR-0068 is the
    ///     specific winner because its content leg also happens to be the closest of the tied set,
    ///     which structure alone cannot produce (structure is one shared "Decision" heading vector
    ///     shared by hundreds of candidates).
    /// </summary>
    private const string Query = "What is the decision?";

    private const string ExpectedTopHash = "dd9fefe6ce20fd84c981170cdeb7cb828a3d3d0d976312813dec794a5f73037e";

    private readonly string _dataRoot;
    private readonly SqliteMemoryStore _store;

    public StructureFusionGateTests()
    {
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-structure-fusion-gate");
        var bundledDb = Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db");
        File.Copy(bundledDb, Path.Combine(_dataRoot, "memory.db"));

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)), new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>
    ///     Gate: vector-only search for <see cref="Query" /> ranks the ADR-0068 Decision chunk first.
    ///     This is the criterion-3 acceptance test — see the WP4 report for the RED capture with
    ///     <see cref="StructureFusion.Fused" /> temporarily forced to ignore the structure term.
    /// </summary>
    [Fact]
    public async Task VectorOnlySearch_GenericDecisionQuery_RanksStructureMatchedChunkFirst()
    {
        var results = await _store.SearchAsync(new SearchQuery(
            ProjectId, Query, SearchScope.Project, Limit: 5, MinScore: 0.0,
            FtsWeight: 0, VectorWeight: 1), TestContext.Current.CancellationToken);

        results.ShouldNotBeEmpty();
        results[0].Hash.ShouldBe(ExpectedTopHash,
            "the fused dual-vector rank must put the structure-matched (heading \"Decision\") chunk " +
            "first for a query this generic on content alone");
        results[0].Ranking.ShouldBe(1.0, 0.0001, "the top fused score is always normalized to 1.0");
    }
}
