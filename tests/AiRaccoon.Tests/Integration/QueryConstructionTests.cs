using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 1 gates (see docs/plans/retrieval-improvement-c.md §3 Wave 1): AND-with-OR-fallback
///     (no zero-matches), the diagnostic triplet
///     and the FTS-only guard on the committed corpus.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class QueryConstructionTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant"; // matches PROJECT_ID in scripts/ingest-jsaa-docs.py
    private const string Adr0070File = "docs:adr:0070-documentation-structure-and-trust-model.md";
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dataRoot;

    private readonly List<string> _extraRoots = [];
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
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>A4 boundary regression: the AND primary at max(TokenCount, limit) rows must fire the OR fallback (measured case).</summary>
    [Fact]
    public async Task AndPrimary_AtBoundary_A4DecisionChunkRestoredByFallback()
    {
        await EnsureModelAsync();
        var query = LoadQueries().First(q => q.Id == "A4");
        var exactHash = LoadHashMap()[query.ExpectedSource!];

        var results = await _store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: 10, MinScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken);

        var rank = results.Select(r => r.Hash).ToList().IndexOf(exactHash) + 1;
        rank.ShouldBeGreaterThan(0,
            "the OR fallback must restore A4's decision chunk to the FTS-only results");
        rank.ShouldBeLessThanOrEqualTo(8,
            $"A4's decision chunk measured FTS-only rank 7 under the fallback (plain OR: 8), got {rank}");
    }

    /// <summary>An AND primary with fewer matches than it has terms is over-constrained — the OR fallback must fire (A6 measured case).</summary>
    [Fact]
    public async Task AndPrimary_UnderMatchedRows_FallsBackToOr()
    {
        await EnsureModelAsync();
        var top5 = await TopHashesAsync("How does the project handle data erasure?", 1, 0,
            TestContext.Current.CancellationToken);
        var rank = FirstFileRank(top5, FileLevel("docs:adr:0067-registry-driven-erasure-with-runtime-verification.md"));
        rank.ShouldNotBeNull("A6's AND primary matches only ADR-0068 rows; the OR fallback must restore ADR-0067");
        rank.Value.ShouldBeLessThanOrEqualTo(RankCutoff, "A6 must answer within the FTS-only top-5 after the fallback");
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
    public async Task DiagnosticTriplet_FtsOnly_AnswersAdr0070WithinTop5()
    {
        await EnsureModelAsync();
        var adr0070 = FileLevel(Adr0070File);

        var q2 = await TopHashesAsync("ADR-0070", 1, 0, TestContext.Current.CancellationToken);
        var q2Rank = FirstFileRank(q2, adr0070);
        q2Rank.ShouldNotBeNull("Q2 'ADR-0070' must find a chunk of the ADR-0070 file in the FTS-only top-5");
        q2Rank.Value.ShouldBeLessThanOrEqualTo(RankCutoff,
            "Q2 'ADR-0070' must answer at FTS-only rank ≤5 (plan C gate, Wave 1.4)");

        foreach (var (id, text) in new[] { ("Q1", "What is ADR-0070 about?"), ("Q3", "documentation structure trust model") })
        {
            var results = await TopHashesAsync(text, 1, 0, TestContext.Current.CancellationToken);
            results.ShouldNotBeEmpty($"{id} must return results on the FTS-only path");
            _output.WriteLine($"{id} '{text}': {results.Count} FTS-only hits, ADR-0070 file rank {FirstFileRank(results, adr0070)?.ToString() ?? "miss"}");
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
            var results = await _store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: query.SearchLimit, MinScore: 0.0), TestContext.Current.CancellationToken);
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
        var hashMap = LoadHashMap();

        var expectedSource = queries.Where(q => !string.IsNullOrWhiteSpace(q.ExpectedSource)).ToArray();
        var adrQueries = expectedSource.Where(q => q.Id.StartsWith("A", StringComparison.Ordinal)).ToArray();

        var adrHitsAt5 = 0;
        var reciprocalRanks = new List<double>();
        foreach (var query in expectedSource)
        {
            var top5 = await TopHashesAsync(query.Query, 1, 0, TestContext.Current.CancellationToken);
            var rank = FirstFileRank(top5, FileLevel(hashMap, query.ExpectedSource!));
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

    /// <summary>No hybrid rank regresses vs the Wave 0 baseline (ranks pinned per plan C §0; see docs/plans/retrieval-improvement-c.md §0, and the documented ADR MRR).</summary>
    [Fact]
    public async Task HybridRanks_DoNotRegress_VsBaseline()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var hashMap = LoadHashMap();

        // Re-pinned 2026-08-06 to the re-pinned corpus (9397bbef; see
        // docs/work/archive/2026-08-06-baseline-repin-new-corpus.md): A6 and C2 are dropped
        // from this comparison; C5's baseline is re-pinned to 5.
        var wave0 = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A1"] = 2, ["A2"] = 1, ["A3"] = 1, ["A4"] = 2, ["A5"] = 1,
            ["A7"] = 1, ["C1"] = 1, ["C5"] = 5
        };

        foreach (var (id, wave0Rank) in wave0)
        {
            var query = queries.First(q => q.Id == id);
            var top5 = await TopHashesAsync(query.Query, 1, 1, TestContext.Current.CancellationToken);
            var rank = FirstFileRank(top5, FileLevel(hashMap, query.ExpectedSource!));
            _output.WriteLine($"{id} hybrid top-5: {string.Join(", ", top5.Select(h => hashMap.FirstOrDefault(p => p.Value == h).Key ?? h))}");
            rank.ShouldNotBeNull($"{id} must still find its expected file (Wave 0 rank {wave0Rank})");
            rank.Value.ShouldBeLessThanOrEqualTo(wave0Rank,
                $"{id} must not regress vs Wave 0 rank {wave0Rank} (plan C gate a), now {rank}");
        }

        // A1/A4 rank flips are same-knowledge alternatives from the dual-vector structure
        // signal, not regressions (docs/adr/0004-dual-vector-structure-signal.md).

        // C2 is dropped from the hybrid dict after the corpus repin; the FTS-only rank-1 check
        // below is its gate (docs/work/archive/2026-08-06-baseline-repin-new-corpus.md).
        var c2 = queries.First(q => q.Id == "C2");
        var c2FtsOnly = await TopHashesAsync(c2.Query, 1, 0, TestContext.Current.CancellationToken);
        var c2FtsRank = FirstFileRank(c2FtsOnly, FileLevel(hashMap, c2.ExpectedSource!));
        c2FtsRank.ShouldBe(1, "C2 must hold at FTS-only rank 1 after the provenance cleanup");
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
        var results = await _store.SearchAsync(new SearchQuery(
            ProjectId, text, SearchScope.Project,
            Limit: SearchLimit, MinScore: 0.0, RrfK: 60,
            FtsWeight: ftsWeight, VectorWeight: vectorWeight), cancellationToken);
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

    private IReadOnlySet<string> FileLevel(string expectedSource)
    {
        var hashMap = LoadHashMap();
        return FileLevel(hashMap, expectedSource);
    }

    private static IReadOnlySet<string> FileLevel(Dictionary<string, string> hashMap, string expectedSource)
    {
        var filePart = expectedSource.Contains('#')
            ? expectedSource[..expectedSource.IndexOf('#')]
            : expectedSource;
        return hashMap
            .Where(pair => pair.Key.StartsWith(filePart, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> LoadHashMap()
    {
        var mapPath = Path.Combine(FindProjectRoot(), "scripts", "chunk-hash-map.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(mapPath), JsonOptions) ?? [];
    }

    private static BaselineQuery[] LoadQueries()
    {
        var queriesPath = Path.Combine(FindProjectRoot(), "scripts", "baseline-queries.json");
        return JsonSerializer.Deserialize<BaselineQuery[]>(
            File.ReadAllText(queriesPath), JsonOptions) ?? [];
    }

    private static string ResolveBundledDbPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db");
        if (File.Exists(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Resources", "jsaa-memory.db"));
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
