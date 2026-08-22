using System.Text.Json;
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
///     Wave 1 gates (see docs/plans/retrieval-improvement-c.md §3 Wave 1): AND-with-OR-fallback
///     (no zero-matches), the diagnostic triplet
///     and the FTS-only guard on the committed corpus.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class QueryConstructionTests : IDisposable
{
    private const string ProjectId = "ai-raccoon"; // matches PROJECT_ID in scripts/src/corpus_config.py
    private const string IdentifierAdrFile = "docs:adr:0075-only-the-server-writes-to-the-bank.md";

    // The bare number, not "ADR-0070": on this corpus an ADR's own heading reads "0075. Only the server writes to the bank", and the file never contains the string
    // "ADR-0070" — only OTHER ADRs citing it do. Querying the prefixed form measured the citation
    // graph, not identifier retrieval.
    private const string IdentifierQuery = "0075";
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dataRoot;

    private readonly List<string> _extraRoots = [];
    private readonly Dictionary<string, HashSet<string>> _fileHashes;
    private readonly Dictionary<string, string> _hashMap;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

    public QueryConstructionTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = TestData.CreateTempRoot();

        var bundledDb = ResolveBundledDbPath();
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(bundledDb, dbPath);

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());

        // Derives structured-path -> hash directly from the regenerated corpus (WP4b,
        // docs/plans/2026-08-14-code-quality-improvement-plan.md) instead of the retired
        // scripts/chunk-hash-map.json. See CorpusHashMap.
        (_hashMap, _fileHashes) = CorpusHashMap.Build(
            dbPath, LoadQueries().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>A4 boundary regression: the AND primary at max(TokenCount, limit) rows must fire the OR fallback (measured case).</summary>
    [Fact]
    public async Task AndPrimary_AtBoundary_A4DecisionChunkRestoredByFallback()
    {
        await EnsureModelAsync();
        var query = LoadQueries().First(q => q.Id == "A4");
        var exactHash = _hashMap[query.ExpectedSource!];

        var results = (await _store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: 10, MinRelativeScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken)).Results;

        var rank = results.Select(r => r.Hash).ToList().IndexOf(exactHash) + 1;
        rank.ShouldBeGreaterThan(0,
            "the OR fallback must restore A4's decision chunk to the FTS-only results");
        rank.ShouldBeLessThanOrEqualTo(8,
            $"A4's decision chunk measured FTS-only rank 7 under the fallback (plain OR: 8), got {rank}");
    }

    /// <summary>
    ///     ai-raccoon#454 re-pin, measured on the public docs corpus 2026-08-22. S2 "What does
    ///     ADR-0006 decide?" tokenizes to {adr, 0006, decide} (TokenCount=3); its AND primary
    ///     "adr AND 0006 AND decide" is over-constrained -- only 2 rows match anywhere in the
    ///     corpus (docs/adr/0088, docs/adr/0078), neither a chunk of ADR-0006. Measured directly
    ///     against the AND-only rows (entries_fts MATCH plan.Expression, OR fallback bypassed):
    ///     ADR-0006 is absent (FirstFileRank -&gt; null). Through the real search path -- AND
    ///     primary plus the automatic OR fallback -- ADR-0006 is restored to rank 1.
    /// </summary>
    [Fact]
    public async Task AndPrimary_UnderMatchedRows_S2Adr0006RestoredByFallback()
    {
        await EnsureModelAsync();
        var query = LoadQueries().First(q => q.Id == "S2");
        var results = (await _store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: 30, MinRelativeScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken)).Results;

        var rank = FirstFileRank([.. results.Select(r => r.Hash)], FileLevel(query.ExpectedSource!));
        rank.ShouldNotBeNull(
            "S2's AND primary ('adr AND 0006 AND decide') matches only docs/adr/0088 and " +
            "docs/adr/0078, excluding ADR-0006 entirely; the OR fallback must restore it");
        rank.Value.ShouldBe(1,
            $"ADR-0006 measured at exactly rank 1 once the OR fallback runs (Limit=30, " +
            $"FtsWeight=1/VectorWeight=0), got {rank}");
    }

    /// <summary>A provably zero-matching AND primary must retry with the OR fallback (results equal the OR-only expression).</summary>
    [Fact]
    public async Task AndPrimary_ZeroMatch_RetriesWithOrFallback()
    {
        await EnsureModelAsync();
        var fallback = await TopHashesAsync("adr zyxwv", 1, 0, TestContext.Current.CancellationToken);
        var plain = await TopHashesAsync("adr", 1, 0, TestContext.Current.CancellationToken);

        fallback.ShouldNotBeEmpty("the OR fallback must rescue an AND zero-match");
        fallback.ShouldBe(plain, "the fallback expression 'adr OR zyxwv' ranks exactly like 'adr'");
    }

    /// <summary>
    ///     Diagnostic triplet (see docs/plans/retrieval-improvement-c.md §3 Wave 1.4): Q2
    ///     (identifier-only) answers on the FTS-only
    ///     path at rank ≤5; Q1 (full question) and Q3 (content-only) return results.
    /// </summary>
    [Fact]
    public async Task DiagnosticTriplet_FtsOnly_AnswersIdentifierAdrWithinTop5()
    {
        await EnsureModelAsync();
        var identifierAdr = FileLevel(IdentifierAdrFile);

        var q2 = await TopHashesAsync(IdentifierQuery, 1, 0, TestContext.Current.CancellationToken);
        var q2Rank = FirstFileRank(q2, identifierAdr);
        q2Rank.ShouldNotBeNull("Q2 'ADR-0070' must find a chunk of the ADR-0070 file in the FTS-only top-5");
        q2Rank.Value.ShouldBeLessThanOrEqualTo(RankCutoff,
            $"Q2 '{IdentifierQuery}' must answer at FTS-only rank <= {RankCutoff} (plan C gate, Wave 1.4)");

        foreach (var (id, text) in new[] { ("Q1", "What is ADR-0075 about?"), ("Q3", "only the server writes to the bank") })
        {
            var results = await TopHashesAsync(text, 1, 0, TestContext.Current.CancellationToken);
            results.ShouldNotBeEmpty($"{id} must return results on the FTS-only path");
            _output.WriteLine($"{id} '{text}': {results.Count} FTS-only hits, identifier ADR file rank {FirstFileRank(results, identifierAdr)?.ToString() ?? "miss"}");
        }
    }

    /// <summary>
    ///     Wave 1 gate (d; see docs/plans/retrieval-improvement-c.md §3 Wave 1): the AND-fallback
    ///     must prevent any zero-match — all 35
    ///     baseline queries return results.
    /// </summary>
    [Fact]
    public async Task AllBaselineQueries_ReturnResults_NoZeroMatch()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var zeroMatches = new List<string>();

        foreach (var query in queries)
        {
            var results = (await _store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: query.SearchLimit, MinRelativeScore: 0.0), TestContext.Current.CancellationToken)).Results;
            if (results.Count == 0)
            {
                zeroMatches.Add(query.Id);
            }
        }

        zeroMatches.ShouldBeEmpty($"every baseline query must return results (zero-match count = 0): {string.Join(", ", zeroMatches)}");
        _output.WriteLine($"Zero-match count across {queries.Length} baseline queries: {zeroMatches.Count}");
    }

    /// <summary>
    ///     Wave 1 gate (c; see docs/plans/retrieval-improvement-c.md §3 Wave 1): no FTS-only
    ///     regression below the status-quo ranker — file
    ///     hit@5 ≥ 6/7 on the ADR suite and file-level MRR ≥ 0.70 on the expected-source suite.
    /// </summary>
    [Fact]
    public async Task FtsOnly_FileHitAt5AndMrr_MeetGuard()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var hashMap = _hashMap;

        var expectedSource = queries.Where(q => !string.IsNullOrWhiteSpace(q.ExpectedSource)).ToArray();
        var adrQueries = expectedSource.Where(q => q.Id.StartsWith("A", StringComparison.Ordinal)).ToArray();

        var adrHitsAt5 = 0;
        var reciprocalRanks = new List<double>();
        foreach (var query in expectedSource)
        {
            var top5 = await TopHashesAsync(query.Query, 1, 0, TestContext.Current.CancellationToken);
            var rank = FirstFileRank(top5, FileLevel(query.ExpectedSource!));
            if (rank is not null)
            {
                reciprocalRanks.Add(1.0 / rank.Value);
            }

            if (query.Id.StartsWith("A", StringComparison.Ordinal) && rank is not null && rank <= RankCutoff)
            {
                adrHitsAt5++;
            }

            _output.WriteLine($"FTS-only {query.Id}: file rank {rank?.ToString() ?? "miss"}");
        }

        var mrr = reciprocalRanks.Count == 0 ? 0.0 : reciprocalRanks.Average();
        adrHitsAt5.ShouldBeGreaterThanOrEqualTo(6,
            $"FTS-only file hit@5 must be ≥ 6/7 on the ADR suite (plan C gate c), got {adrHitsAt5}/{adrQueries.Length}");
        mrr.ShouldBeGreaterThanOrEqualTo(0.70,
            $"FTS-only file MRR must be ≥ 0.70 on the expected-source suite (plan C gate c), got {mrr:F4}");
        _output.WriteLine($"FTS-only guard: ADR file hit@5 {adrHitsAt5}/{adrQueries.Length}, MRR {mrr:F4} over {expectedSource.Length} queries");
    }

    /// <summary>
    ///     No hybrid rank regresses vs the Wave 0 baseline (ranks pinned per plan C §0; see
    ///     docs/plans/retrieval-improvement-c.md §0, and the documented ADR MRR).
    /// </summary>
    /// <remarks>
    ///     KNOWN REGRESSION (WP3b) for A3, A7 and C2 only, not a passing guarantee for those
    ///     three. Before the 2026-08-14 corpus regeneration all eight wave0 ids held or beat
    ///     their pinned rank, and C2 held FTS-only rank 1. On the 3.3x denser corpus A3 and A7
    ///     regressed from rank &lt;= 1 to exactly rank 3, and C2 regressed from FTS-only rank 1 to
    ///     exactly rank 3 -- pinned exactly so the suite stays honest: when ranking improves any
    ///     of the three, that specific assertion FAILS, which is the signal to move that id back
    ///     under the no-regression loop (or restore c2FtsRank == 1) and delete this note. Do not
    ///     "fix" by raising a ceiling instead. A1, A2, A4, A5, C1 and C5 are unaffected and still
    ///     assert genuine no-regression.
    /// </remarks>
    [Fact]
    public async Task HybridRanks_DoNotRegress_VsBaseline_DocumentsKnownRankRegressions()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var hashMap = _hashMap;

        // Measured on the public docs corpus, 2026-08-22 (ADR-0090). These are this corpus's
        // OWN baseline, not a port: the previous table recorded ranks against the private jsaa
        // corpus and a "WP3b known regression" band that has no counterpart here, because there
        // is no earlier measurement on this corpus to have regressed from. Ceilings, so an
        // improvement never goes red; a slide does.
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A1"] = 3, ["A2"] = 1, ["A3"] = 4, ["A4"] = 1, ["A5"] = 1,
            ["A7"] = 1, ["C1"] = 3, ["C5"] = 3
        };

        foreach (var (id, baselineRank) in baseline)
        {
            var query = queries.First(q => q.Id == id);
            var top5 = await TopHashesAsync(query.Query, 1, 1, TestContext.Current.CancellationToken);
            var rank = FirstFileRank(top5, FileLevel(query.ExpectedSource!));
            _output.WriteLine($"{id} hybrid top-5: {string.Join(", ", top5.Select(h => hashMap.FirstOrDefault(p => p.Value == h).Key ?? h))}");
            rank.ShouldNotBeNull($"{id} must find its expected file (baseline rank {baselineRank})");
            rank.Value.ShouldBeLessThanOrEqualTo(baselineRank,
                $"{id} must not regress past its measured baseline rank {baselineRank} on the public " +
                $"docs corpus (ADR-0090), now {rank}");
        }

        // A1/A4 rank flips are same-knowledge alternatives from the dual-vector structure
        // signal, not regressions (docs/adr/0004-dual-vector-structure-signal.md).

        // C2 is dropped from the hybrid dict after the corpus repin; the FTS-only rank-1 check
        // below is its gate (docs/work/archive/2026-08-06-baseline-repin-new-corpus.md).
        //
        // KNOWN REGRESSION (WP3b, 2026-08-14): C2 held FTS-only rank 1 after the provenance
        // cleanup; on the denser corpus it is pinned at exactly rank 3.
        var c2 = queries.First(q => q.Id == "C2");
        var c2FtsOnly = await TopHashesAsync(c2.Query, 1, 0, TestContext.Current.CancellationToken);
        var c2FtsRank = FirstFileRank(c2FtsOnly, FileLevel(c2.ExpectedSource!));
        c2FtsRank.ShouldBe(3,
            "known regression (WP3b): C2 held FTS-only rank 1; now pinned at exactly rank 3 -- see " +
            "docs/work/2026-08-14-retrieval-rank-regressions.md; invert when WP3b lands");
    }

    // ------------------------------------------------------------------ helpers

    private async Task EnsureModelAsync()
    {
        var ensured = await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        ensured.AllPresent.ShouldBeTrue($"bundled embedding model must be provisioned: {string.Join("; ", ensured.Errors)}");
    }

    private async Task<IReadOnlyList<string>> TopHashesAsync(
        string text, int ftsWeight, int vectorWeight, CancellationToken cancellationToken)
    {
        var results = (await _store.SearchAsync(new SearchQuery(
            ProjectId, text, SearchScope.Project,
            Limit: SearchLimit, MinRelativeScore: 0.0, RrfK: 60,
            FtsWeight: ftsWeight, VectorWeight: vectorWeight), cancellationToken)).Results;
        return [.. results.Take(RankCutoff).Select(result => result.Hash)];
    }

    private static int? FirstFileRank(IReadOnlyList<string> topHashes, IReadOnlySet<string> fileHashes)
    {
        for (var i = 0; i < topHashes.Count; i++)
        {
            if (fileHashes.Contains(topHashes[i]))
            {
                return i + 1;
            }
        }

        return null;
    }

    private IReadOnlySet<string> FileLevel(string expectedSource) => _fileHashes[CorpusHashMap.FileKey(expectedSource)];

    private static BaselineQuery[] LoadQueries()
    {
        var queriesPath = Path.Combine(FindProjectRoot(), "scripts", "baseline-queries.json");
        return JsonSerializer.Deserialize<BaselineQuery[]>(
            File.ReadAllText(queriesPath), JsonOptions) ?? [];
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

    private sealed record BaselineQuery(string Id, string Query, string? ExpectedSource, int SearchLimit);
}
