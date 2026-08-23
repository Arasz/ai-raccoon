using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     The acceptance test for WP4 (docs/plans/2026-08-14-code-quality-improvement-plan.md): the
///     regenerated corpus (tests/AiRaccoon.Tests/Resources/docs-memory.db) is the first committed
///     gate fixture to carry real structure vectors (docs/adr/0004-dual-vector-structure-signal.md),
///     so it is the first fixture that can prove <see cref="StructureFusion" /> is load-bearing —
///     deliberately breaking it must make a gate here go red.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class StructureFusionGateTests : IDisposable
{
    private const string ProjectId = "ai-raccoon";

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

    /// <summary>
    ///     The chunk this gate expects on top, addressed by what makes it the right answer — the
    ///     "Decision" heading of ADR-0068 — rather than by hash. Hashes are derived from the row's
    ///     path (ContentHash.Of(path, chunk)), so pinning one breaks on every corpus regeneration
    ///     for reasons that have nothing to do with what the gate tests. WP4c proved that: fixing
    ///     the corpus to store repo-relative paths changed every hash while this chunk stayed
    ///     exactly the same file and heading.
    /// </summary>
    private const string ExpectedSourceFile = "docs/adr/0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpus.md";

    private const string ExpectedHeading = "Decision";

    private readonly ITestOutputHelper _output;
    private readonly string _dataRoot;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public StructureFusionGateTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-structure-fusion-gate");
        var bundledDb = Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db");
        File.Copy(bundledDb, Path.Combine(_dataRoot, "memory.db"));

        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
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
        var results = (await _store.SearchAsync(new SearchQuery(
            ProjectId, Query, SearchScope.Project, Limit: 5, MinRelativeScore: 0.0,
            FtsWeight: 0, VectorWeight: 1), TestContext.Current.CancellationToken)).Results;

        results.ShouldNotBeEmpty();
        await using (var probe = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var r in results)
            {
                var row = await probe.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
                    "SELECT source_file || '  [' || COALESCE(heading_path,'-') || ']' FROM entries WHERE hash = @h",
                    new { h = r.Hash }, cancellationToken: TestContext.Current.CancellationToken));
                _output.WriteLine($"PROBE {r.Ranking:F4}  {row}");
            }
        }

        var expectedHash = await ExpectedTopHashAsync();
        results[0].Hash.ShouldBe(expectedHash,
            "the fused dual-vector rank must put the structure-matched (heading \"Decision\") chunk " +
            "first for a query this generic on content alone");
        results[0].Ranking.ShouldBe(1.0, 0.0001, "the top fused score is always normalized to 1.0");
    }

    /// <summary>Resolves the expected chunk from the corpus, so a regeneration cannot stale it.</summary>
    private async Task<string> ExpectedTopHashAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var hash = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            "SELECT hash FROM entries WHERE source_file = @file AND heading_path = @heading LIMIT 1",
            new { file = ExpectedSourceFile, heading = ExpectedHeading },
            cancellationToken: TestContext.Current.CancellationToken));
        hash.ShouldNotBeNull($"the corpus must still contain '{ExpectedHeading}' in {ExpectedSourceFile}");
        return hash;
    }
}
