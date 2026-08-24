using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 2 gates of docs/plans/retrieval-improvement-c.md: search results carry source
///     identity, FTS-only queries resolve via the source column, and invariants C1/C2/C5
///     hold their pinned hybrid ranks.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class SourceIdentityTests : IDisposable
{
    private const string ProjectId = "ai-raccoon"; // matches PROJECT_ID in scripts/src/corpus_config.py

    private const string AdrDecision = "docs:adr:0006-rrf-parameter-optimization.md#decision";
    private const string AdrSource = "docs/adr/0006-rrf-parameter-optimization.md";

    // The identity assertions below need a hit whose retrieval is not itself in question: this
    // test proves results CARRY source identity, so a target that ranks poorly would fail it for
    // the wrong reason. ADR-0031's Consequences chunk answers its query at rank 1 (catalog S3).
    private const string IdentityChunk = "docs:adr:0031-polly-resilience-pipelines.md#consequences";
    private const string IdentitySource = "docs/adr/0031-polly-resilience-pipelines.md";
    private const string IdentityQuery = "What retry pipeline does the asset downloader use?";
    private const string IdentifierAdrDecision = "docs:adr:0070-maintenance-is-a-list-of-jobs-with-a-ledger.md#decision";
    private const string IdentifierAdrSource = "docs/adr/0070-maintenance-is-a-list-of-jobs-with-a-ledger.md";
    private const string InvariantTdd = "ai-badger:invariants/tdd-mandatory.md";
    private const string InvariantScreaming = "ai-badger:invariants/screaming-architecture.md";
    private const string InvariantProveCheck = "ai-badger:invariants/prove-the-check-fails.md";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly Dictionary<string, string> _hashMap;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;
    private readonly SqliteMemoryStore _pinnedStore;

    public SourceIdentityTests(ITestOutputHelper output)
    {
        _output = output;

        // The regenerated DB carries embedding.provider='local', so SearchAsync embeds the
        // query at query time; provision the bundled ONNX model before any search.
        var ensured = TestData.CreateBundledModel().EnsureAsync().GetAwaiter().GetResult();
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        _dataRoot = TestData.CreateTempRoot("ai-raccoon-source-identity");
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(ResolveBundledDbPath(), dbPath);

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

        // The S2 gate searches a second copy with pinned query vectors (ADR-0050 pattern): the
        // bundled u8s8 model puts ADR-0006 exactly on this gate's rank-3/4 boundary, so the live
        // embedding made the verdict a function of the host CPU — arm64 passes at 3, VNNI/non-VNNI
        // x64 flips to 4 (nightly #575, docs/adr/0049). The other gates keep the live path.
        var pinnedRoot = TestData.CreateTempRoot("ai-raccoon-source-identity-pinned");
        var pinnedDbPath = Path.Combine(pinnedRoot, "memory.db");
        File.Copy(ResolveBundledDbPath(), pinnedDbPath);
        var pinnedFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = pinnedRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = pinnedRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _pinnedStore = TestData.CreateMemoryStore(pinnedFactory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(pinnedFactory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            PinnedQueryVectors.EmbeddingService(), null, null, null, null, null, null, null);

        // Derives structured-path -> hash directly from the regenerated corpus (WP4b,
        // docs/plans/2026-08-14-code-quality-improvement-plan.md) instead of the retired
        // scripts/chunk-hash-map.json. See CorpusHashMap.
        (_hashMap, _) = CorpusHashMap.Build(dbPath,
        [
            AdrDecision, IdentifierAdrDecision, IdentityChunk, InvariantTdd, InvariantScreaming, InvariantProveCheck
        ]);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task SearchResults_CarrySourceIdentity_ForIngestedChunks()
    {
        var expectedHash = _hashMap[IdentityChunk];

        var results = (await _store.SearchAsync(new SearchQuery(ProjectId,
            IdentityQuery,
            SearchScope.Project, Limit: 10, MinRelativeScore: 0.0), TestContext.Current.CancellationToken)).Results;

        var hit = results.FirstOrDefault(r => r.Hash == expectedHash);
        hit.ShouldNotBeNull("the expected decision chunk must appear in the top 10");
        hit.SourceFile.ShouldBe(IdentitySource,
            "the result must carry the original relative path of its source file");
        hit.TotalChunks.ShouldBeGreaterThanOrEqualTo(2, "the expected ADR is chunked into several sections");
        hit.ChunkIndex.ShouldBeInRange(0, hit.TotalChunks - 1, "ChunkIndex is 0-based within the source");

        foreach (var result in results.Where(r => r.SourceFile is not null))
        {
            result.TotalChunks.ShouldBeGreaterThanOrEqualTo(1);
            result.ChunkIndex.ShouldBeInRange(0, result.TotalChunks - 1);
        }
    }

    /// <summary>S2 (docs/plans/retrieval-improvement-c.md §3 Wave 2): the Decision-chunk's own rank is logged, not asserted — Wave 6's dual-vector structure signal is the target for that. Searches the pinned-vector copy (ADR-0050 pattern): with the live query embedding this verdict was a function of the host CPU — arm64 passes at 3, x64 flips to 4 (docs/adr/0049; nightly reds #527/#575).</summary>
    [Fact]
    public async Task S2_SectionQuery_FindsItsFileWithinTop3_AndLogsDecisionChunkRank()
    {
        var hashMap = _hashMap;

        var results = (await _pinnedStore.SearchAsync(new SearchQuery(ProjectId, "What does ADR-0006 decide?",
            SearchScope.Project, Limit: 20, MinRelativeScore: 0.0), TestContext.Current.CancellationToken)).Results;

        var (fileHit, fileRank) = FindRank(results, r => string.Equals(r.SourceFile, AdrSource, StringComparison.Ordinal));
        fileHit.ShouldNotBeNull("S2: a chunk of the expected ADR must appear in the top 20");
        fileRank.ShouldBeInRange(1, 3, "S2 gate: the expected ADR at rank <= 3 (source-column fix)");

        var (decisionHit, decisionRank) = FindRank(results, r => r.Hash == hashMap[AdrDecision]);
        _output.WriteLine($"S2: Decision-section chunk at hybrid rank {(decisionHit is null ? "not found" : decisionRank.ToString())} (Wave 6 target: <= 3)");
    }

    /// <summary>Q2 (docs/plans/retrieval-improvement-c.md §3 Wave 2): the decision chunk's rank is logged, not asserted — the header chunk legitimately outranks it for a bare identifier.</summary>
    [Fact]
    public async Task Q2_IdentifierOnly_FtsOnlyFileRankWithinTop3()
    {
        var hashMap = _hashMap;

        var results = (await _store.SearchAsync(new SearchQuery(ProjectId, "ADR-0070",
                SearchScope.Project, Limit: 10, MinRelativeScore: 0.0, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken)).Results;

        var (fileHit, fileRank) = FindRank(results, r => string.Equals(r.SourceFile, IdentifierAdrSource, StringComparison.Ordinal));
        fileHit.ShouldNotBeNull("Q2: a chunk of ADR-0070 must appear in the FTS-only top 10");
        fileRank.ShouldBeInRange(1, 3, "Q2 gate: ADR-0070 at FTS-only rank <= 3 (source-column fix)");

        var (decisionHit, decisionRank) = FindRank(results, r => r.Hash == hashMap[IdentifierAdrDecision]);
        _output.WriteLine($"Q2: Decision-section chunk at FTS-only rank {(decisionHit is null ? "not found" : decisionRank.ToString())}");
    }

    /// <summary>
    ///     A "file#section" anchor ANDs against the FTS {source_file section} columns, so it can
    ///     only resolve for chunks whose section was written at ingest. Restored to the production
    ///     Limit=5 / rank 1 assertion once FileIngestor populated section from the heading leaf.
    /// </summary>
    [Fact]
    public async Task SourcePathQuery_ReturnsTheExactChunkFirst()
    {
        var hashMap = _hashMap;

        var results = (await _store.SearchAsync(new SearchQuery(ProjectId,
            "docs/adr/0006-rrf-parameter-optimization.md#decision",
            SearchScope.Project, Limit: 5, MinRelativeScore: 0.0), TestContext.Current.CancellationToken)).Results;

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[AdrDecision]);
        hit.ShouldNotBeNull("the anchored chunk must be found within the production Limit=5 window");
        rank.ShouldBe(1, "a source-path anchor names one chunk exactly; anything else means the " +
                         "anchor is not reaching the section column");
    }

    /// <summary>
    ///     Re-measured on the public docs corpus (ADR-0090). C1's floor moves 1 -> 3 and C5's
    ///     stays 5. C1 is NOT a widened bound: on the jsaa corpus the TDD invariant was the only
    ///     document that discussed testing policy, while this repo carries tdd-mandatory.md
    ///     alongside minimal-test-runs.md, prove-the-check-fails.md and a testing-heavy CLAUDE.md,
    ///     which legitimately share the top of that query. The pins are the measured
    ///     no-regression floor, not an aspiration.
    /// </summary>
    [Theory]
    [InlineData("Is TDD required?", InvariantTdd, 3)]
    [InlineData("Must a check be seen failing before it counts?", InvariantProveCheck, 5)]
    public async Task InvariantQueries_C1C5_HoldMeasuredHybridRanks(string query, string expectedSource,
        int expectedRank)
    {
        var hashMap = _hashMap;

        var results = (await _store.SearchAsync(new SearchQuery(ProjectId, query,
            SearchScope.Project, Limit: 5, MinRelativeScore: 0.0), TestContext.Current.CancellationToken)).Results;

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[expectedSource]);
        hit.ShouldNotBeNull($"{expectedSource} must appear in the top 5");
        rank.ShouldBeLessThanOrEqualTo(expectedRank,
            $"invariant {expectedSource} must hold its measured hybrid rank {expectedRank} (no further regression)");
    }

    /// <summary>
    ///     C2's hybrid rank collapsed once clean content dropped the embedded provenance prefix
    ///     (docs/plans/retrieval-improvement-c.md §3 2d): a vector rank &gt;100 sinks a perfect FTS
    ///     rank 1 in RRF fusion, so this pins FTS-only rank 1 instead (fusion weighting: docs/adr/0006-rrf-parameter-optimization.md).
    /// </summary>
    /// <remarks>
    ///     KNOWN REGRESSION (WP3b), not a passing guarantee. Before the 2026-08-14 corpus
    ///     regeneration the invariant held FTS-only rank 1; on the 3.3x denser corpus it ranks
    ///     3. Asserted exactly so the suite stays honest: when ranking improves this test FAILS,
    ///     and that failure is the signal to restore the original assertion (rank == 1) and
    ///     delete this note. Do not "fix" it by widening the bound.
    /// </remarks>
    [Fact]
    public async Task InvariantC2_ScreamingArchitecture_DocumentsKnownFtsOnlyRankRegression()
    {
        var hashMap = _hashMap;

        var results = (await _store.SearchAsync(new SearchQuery(ProjectId,
                "What is the screaming architecture rule?",
                SearchScope.Project, Limit: 5, MinRelativeScore: 0.0, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken)).Results;

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[InvariantScreaming]);
        hit.ShouldNotBeNull("the screaming-architecture invariant must appear in the FTS-only top 5");
        rank.ShouldBe(3,
            "known regression (WP3b): previously FTS-only rank 1, now pinned at exactly rank 3 -- see " +
            "docs/work/2026-08-14-retrieval-rank-regressions.md; invert when WP3b lands");
    }

    private static (MemorySearchResult? Hit, int Rank) FindRank(
        IReadOnlyList<MemorySearchResult> results, Func<MemorySearchResult, bool> predicate)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (predicate(results[i]))
            {
                return (results[i], i + 1);
            }
        }

        return (null, 0);
    }


    private static string ResolveBundledDbPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db");
        if (File.Exists(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Resources", "docs-memory.db"));
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
