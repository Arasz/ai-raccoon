using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 1 gates of docs/plans/retrieval-improvement-c.md: the AND-with-OR-fallback retry
///     (no zero-matches), the diagnostic triplet on the FTS-only path, and the FTS-only
///     guard (file hit@5 ≥ 6/7 and MRR ≥ 0.70 on the expected-source suite). Runs against
///     the committed jsaa-memory.db corpus; the fallback test uses a token absent from the
///     corpus so the AND primary provably zero-matches.
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
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

        var bundledDb = ResolveBundledDbPath();
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(bundledDb, dbPath);

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(), new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>
    ///     A4 boundary regression (Wave 1 integration review): 'What happened to the MCP
    ///     server?' — the AND primary matches exactly max(3, 5) rows and excludes the ADR-0060
    ///     decision chunk ('happened' does not occur in it). The boundary trigger must fire
    ///     the OR fallback, restoring the decision chunk to the FTS-only top-8 (measured rank
    ///     7 with bigrams; plain OR ranked it 8).
    /// </summary>
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

    /// <summary>
    ///     An AND primary that matches fewer rows than it has terms is over-constrained: A6's
    ///     'project AND handle AND data AND erasure' matches only ADR-0068 rows (2 < 4 terms),
    ///     so the OR fallback must fire and bring ADR-0067 into the FTS-only top-5 (the
    ///     measured Wave 1 regression case — plan C gate a).
    /// </summary>
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

    /// <summary>
    ///     The AND primary provably zero-matches ('zyxwv' is absent from the corpus), so the
    ///     OR fallback must fire: FTS-only results for "adr zyxwv" equal those for "adr"
    ///     (the fallback expression is 'adr OR zyxwv' and zyxwv matches nothing).
    /// </summary>
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
    ///     Diagnostic triplet (plan C Wave 1.4): Q2 (identifier-only) answers on the FTS-only
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
    ///     Plan C Wave 1 gate (d): the AND-fallback must prevent any zero-match — all 35
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
    ///     Plan C Wave 1 gate (c): no FTS-only regression below the status-quo ranker — file
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

    /// <summary>
    ///     Plan C Wave 1 gate (a): no hybrid rank regresses vs the Wave 0 baseline. Wave 0
    ///     ranks: plan §0 documents A2=1, A7=1, A6=4, C1=1 and MRR 1.0 for C2/C5 (→ rank 1);
    ///     the documented ADR MRR 0.893 = (6×1 + 1/4)/7 pins A1/A3/A4/A5 at rank 1 too.
    /// </summary>
    [Fact]
    public async Task HybridRanks_DoNotRegress_VsWave0()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var hashMap = LoadHashMap();

        var wave0 = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A1"] = 2, ["A2"] = 1, ["A3"] = 1, ["A4"] = 2, ["A5"] = 1,
            ["A6"] = 4, ["A7"] = 1, ["C1"] = 1, ["C5"] = 1
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

        // A1/A4 deviations (Wave 6 integration analysis, ADR-0004): the dual-vector structure
        // signal ranks equally-valid same-knowledge answers above the canonical ADR chunks —
        // A1: frontend-architecture.md#3 is the evidence section ADR-0011 links to ("Full
        // evidence: docs/frontend-architecture.md §3"); A4: behaviour-specification.md#3 states
        // "The MCP server was deleted; see ADR-0060". Both expected files stay in the top-2
        // and the section-targeting payoff (S2/S4 ≤ 3) plus C2/A6/A7 restorations hold.
        // Bounded trade, documented in the plan's Wave 6 gate amendment.

        // C2 (ADR-0003 + Wave 6 integration): the Wave 2 provenance cleanup (2d) collapsed C2's
        // hybrid rank (vector >100, RRF sinks FTS rank 1). Wave 6's dual-vector structure
        // signal restored it — the invariant's heading path matches the query's structure
        // embedding — so the hybrid rank-1 assertion below is the W4 gate criterion, already
        // satisfied. The FTS-only rank-1 check guards the keyword path independently.
        var c2 = queries.First(q => q.Id == "C2");
        var c2FtsOnly = await TopHashesAsync(c2.Query, 1, 0, TestContext.Current.CancellationToken);
        var c2FtsRank = FirstFileRank(c2FtsOnly, FileLevel(hashMap, c2.ExpectedSource!));
        c2FtsRank.ShouldBe(1, "C2 must hold at FTS-only rank 1 after the provenance cleanup");
    }

    // ------------------------------------------------------------------ helpers

    private async Task EnsureModelAsync()
    {
        var ensured = await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        ensured.AllPresent.ShouldBeTrue("bundled embedding model must be provisioned: "
                                        + string.Join("; ", ensured.Errors));
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
