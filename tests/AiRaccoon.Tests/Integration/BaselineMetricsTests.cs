using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Gate of docs/plans/retrieval-improvement-c.md: wires retrieval metrics to the JSAA
///     baseline across hybrid/FTS-only/vector-only per-query runs, scored via RetrievalMetrics.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class BaselineMetricsTests : IDisposable
{
    private const string ProjectId = "ai-raccoon"; // matches PROJECT_ID in scripts/src/corpus_config.py
    private const string ReportFileName = "baseline-metrics-report.json";

    /// <summary>Metric cutoff: nDCG@5 and recall@5 both grade the top-5 window.</summary>
    private const int RankCutoff = 5;

    /// <summary>
    ///     Final merged list depth per modality run: the RRF candidate window is
    ///     max(limit*3, 100), so this keeps plenty of overlap candidates for the top-5 metrics.
    /// </summary>
    private const int SearchLimit = 10;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataRoot;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

    public BaselineMetricsTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = CreateTempRoot();

        var bundledDb = ResolveBundledDbPath();
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(bundledDb, dbPath);

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Provisions the bundled embedding model before any search that can embed; fails loudly
    ///     instead of surfacing a late model-path error.
    /// </summary>
    private async Task EnsureModelAsync()
    {
        var ensured = await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        ensured.AllPresent.ShouldBeTrue(
            $"bundled embedding model must be provisioned before vector/hybrid searches: {string.Join("; ", ensured.Errors)}");
    }

    /// <summary>
    ///     Wave 0 gate (docs/plans/retrieval-improvement-c.md §0): computes per-query nDCG@5 /
    ///     MRR / recall@5 with modality attribution and writes the determinism-target report.
    /// </summary>
    [RetryFact]
    public async Task RunBaseline_ComputesMetricsAndWritesReport()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var relevance = BuildRelevanceSets(queries);
        var stats = await _store.GetStatsAsync(ProjectId, TestContext.Current.CancellationToken);
        var provider = await ProbeProviderAsync(TestContext.Current.CancellationToken);

        var metrics = new List<QueryMetrics>(queries.Length);
        foreach (var query in queries)
        {
            metrics.Add(await EvaluateAsync(query, relevance.FileLevel(query.ExpectedSource),
                TestContext.Current.CancellationToken));
        }

        var evaluated = metrics.Where(m => relevance.FileLevel(
            queries.First(q => q.Id == m.Id).ExpectedSource).Count > 0).ToList();
        evaluated.Count.ShouldBe(19,
            "all 19 expected-source queries (A1-A10, S1-S6, C1/C2/C5) should be gradeable via the corpus-derived hash map (CorpusHashMap)");

        // The [0,1] range assertions that stood here are deleted (WP11, docs/adr/0056): nDCG, MRR
        // and recall return a value in [0,1] by construction for any input, so they reported success
        // for a reversed ranking. What can fail lives in HeldOutRetrievalGateTests, over the queries
        // no parameter sweep tuned on; this test keeps only the finiteness check, which a NaN from a
        // degenerate ideal-DCG would trip, and remains the determinism/report gate it was written as.
        foreach (var metric in metrics)
        {
            double.IsFinite(metric.Ndcg5).ShouldBeTrue($"nDCG@5 for {metric.Id} must be finite");
            double.IsFinite(metric.Mrr).ShouldBeTrue($"MRR for {metric.Id} must be finite");
            double.IsFinite(metric.Recall5).ShouldBeTrue($"recall@5 for {metric.Id} must be finite");
        }

        // Fusion-regression observations (docs/plans/retrieval-improvement-c.md §3 Wave 4): logged
        // as a data point, not asserted — Wave 0's gate is reproducibility and determinism only.
        var regressions = new List<string>();
        foreach (var metric in evaluated)
        {
            var relevant = relevance.FileLevel(QueryById(queries, metric.Id).ExpectedSource);
            var hybridRecall = RetrievalMetrics.RecallAtK(metric.HybridTop5Hashes, relevant, RankCutoff);
            var ftsRecall = RetrievalMetrics.RecallAtK(metric.FtsTop5Hashes, relevant, RankCutoff);
            var vectorRecall = RetrievalMetrics.RecallAtK(metric.VectorTop5Hashes, relevant, RankCutoff);
            if (hybridRecall < Math.Max(ftsRecall, vectorRecall))
            {
                regressions.Add($"{metric.Id}: hybrid {hybridRecall:F3} < max(fts {ftsRecall:F3}, vector {vectorRecall:F3})");
            }
        }

        if (regressions.Count > 0)
        {
            _output.WriteLine($"[INFO] Fusion regression observed ({regressions.Count} queries): {string.Join("; ", regressions)}");
        }

        var categories = BuildCategoryAggregates(queries, metrics, relevance);

        // Wave 5b gate (docs/plans/retrieval-improvement-c.md §3 Wave 5b): every category must
        // be covered, including Structural (S1-S6) and ADR (A1-A10).
        var structural = categories.Single(c => c.Category == "Structural (Section-Targeted)");
        structural.QueryCount.ShouldBe(6, "S1-S6 must all be in the Structural category");
        structural.EvaluatedQueryCount.ShouldBe(6, "all six Structural queries must be gradeable");
        var adr = categories.Single(c => c.Category == "Architecture Decisions (ADR)");
        adr.QueryCount.ShouldBe(10, "A1-A10 must all be in the ADR category");
        adr.EvaluatedQueryCount.ShouldBe(10, "all ten ADR queries must be gradeable");
        var report = new BaselineMetricsReport(
            FixedNow.ToString("O"),
            stats.EntryCount,
            stats.EntryCount - stats.PendingCount,
            provider,
            queries.Length,
            evaluated.Count,
            DeterminismHash(metrics),
            [.. metrics.OrderBy(m => m.Id, StringComparer.Ordinal)],
            categories);

        var reportPath = Path.Combine(AppContext.BaseDirectory, ReportFileName);
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, JsonOptions), TestContext.Current.CancellationToken);

        var roundTripped = JsonSerializer.Deserialize<BaselineMetricsReport>(
            await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken), JsonOptions);
        roundTripped.ShouldNotBeNull();
        roundTripped.Queries.Count.ShouldBe(report.Queries.Count, "round-trip must preserve every query");
        roundTripped.DeterminismHash.ShouldBe(report.DeterminismHash,
            "round-trip must preserve the determinism hash");
        roundTripped.EvaluatedQueryCount.ShouldBe(report.EvaluatedQueryCount);

        _output.WriteLine($"DB: {stats.EntryCount} entries, {stats.EntryCount - stats.PendingCount} embedded, provider={provider ?? "none"}");
        foreach (var category in categories)
        {
            _output.WriteLine(
                $"  {category.Category}: nDCG@5={category.Ndcg5:F3} MRR={category.Mrr:F3} recall@5={category.Recall5:F3} ({category.EvaluatedQueryCount}/{category.QueryCount} evaluated)");
        }

        var missing = metrics.Where(m => m.ExpectedFileFoundBy == "none").Select(m => m.Id).ToList();
        _output.WriteLine(missing.Count == 0
            ? "Expected file found in top-5 by at least one modality for every evaluated query"
            : $"Expected file NOT found by any modality: {string.Join(", ", missing)}");
        _output.WriteLine($"Report written to {reportPath}");
    }

    /// <summary>
    ///     The determinism gate: two consecutive hybrid passes over the full query set must
    ///     produce identical top-5 hash sequences per query.
    /// </summary>
    [RetryFact]
    public async Task DoubleRun_ProducesIdenticalTop5SequencesPerQuery()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var firstPass = new List<IReadOnlyList<string>>(queries.Length);
        var secondPass = new List<IReadOnlyList<string>>(queries.Length);

        foreach (var query in queries)
        {
            firstPass.Add(await TopHashesAsync(query.Query, 1, 1, TestContext.Current.CancellationToken));
        }

        foreach (var query in queries)
        {
            secondPass.Add(await TopHashesAsync(query.Query, 1, 1, TestContext.Current.CancellationToken));
        }

        var mismatches = new List<string>();
        var withFullDepth = 0;
        for (var i = 0; i < queries.Length; i++)
        {
            if (firstPass[i].Count == RankCutoff)
            {
                withFullDepth++;
            }

            if (!firstPass[i].SequenceEqual(secondPass[i]))
            {
                mismatches.Add(queries[i].Id);
            }
        }

        mismatches.ShouldBeEmpty(
            $"identical top-5 hashes per query across two consecutive runs; run1 vs run2: {string.Join("; ", mismatches.Select(id =>
            {
                var i = Array.FindIndex(queries, q => q.Id == id);
                return $"{id}(run1={string.Join(",", firstPass[i])} run2={string.Join(",", secondPass[i])})";
            }))}");
        firstPass.ShouldNotBeEmpty("the query set must be non-empty");

        _output.WriteLine($"Determinism double-run: {queries.Length - mismatches.Count}/{queries.Length} queries identical, {withFullDepth}/{queries.Length} returned a full top-5");
    }

    /// <summary>
    ///     RED on the old committed DB (no embeddings, vector modality absent); GREEN once the
    ///     regenerated DB (provider='local', all embedded) lands.
    /// </summary>
    [RetryFact]
    public async Task VectorOnly_SearchReturnsRankedResults_OnEmbeddedDatabase()
    {
        await EnsureModelAsync();
        var queries = LoadQueries();
        var probe = queries.First(q => q.Id == "A1");
        var results = await TopHashesAsync(probe.Query, 0, 1, TestContext.Current.CancellationToken);

        results.Count.ShouldBeGreaterThanOrEqualTo(1,
            "vector-only search returned no results — the database has no embedding provider or " +
            "vectors configured. The regenerated docs-memory.db (Wave 0 step 3) must set " +
            "provider='local' with 100% embedded rows; the old committed DB (6675 entries, 0 " +
            "embeddings) is expected to fail this test.");
    }


    private async Task<QueryMetrics> EvaluateAsync(
        BaselineQuery query, IReadOnlySet<string> relevant, CancellationToken cancellationToken)
    {
        var hybrid = await TopHashesAsync(query.Query, 1, 1, cancellationToken);
        var fts = await TopHashesAsync(query.Query, 1, 0, cancellationToken);
        var vector = await TopHashesAsync(query.Query, 0, 1, cancellationToken);

        var (foundBy, hybridRank) = AttributeModality(relevant, hybrid, fts, vector);
        return new QueryMetrics(
            query.Id,
            query.Category,
            query.Query,
            hybrid,
            fts,
            vector,
            RetrievalMetrics.NdcgAtK(hybrid, relevant, RankCutoff),
            RetrievalMetrics.Mrr(hybrid, relevant),
            RetrievalMetrics.RecallAtK(hybrid, relevant, RankCutoff),
            foundBy,
            hybridRank);
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

    /// <summary>
    ///     Modality attribution: which modality ranked the expected file first (lowest rank of the
    ///     first relevant hash in top-5). Hybrid wins ties; 'none' means no modality found it, null
    ///     means the query has no relevance set.
    /// </summary>
    private static (string? FoundBy, int? HybridRank) AttributeModality(
        IReadOnlySet<string> relevant,
        IReadOnlyList<string> hybrid,
        IReadOnlyList<string> fts,
        IReadOnlyList<string> vector)
    {
        if (relevant.Count == 0)
        {
            return (null, null);
        }

        int? FirstRankOf(IReadOnlyList<string> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (relevant.Contains(list[i]))
                {
                    return i + 1;
                }
            }

            return null;
        }

        var hybridRank = FirstRankOf(hybrid);
        var ftsRank = FirstRankOf(fts);
        var vectorRank = FirstRankOf(vector);

        var candidates = new List<(string Name, int Rank)>();
        if (hybridRank is not null)
        {
            candidates.Add(("hybrid", hybridRank.Value));
        }

        if (ftsRank is not null)
        {
            candidates.Add(("fts", ftsRank.Value));
        }

        if (vectorRank is not null)
        {
            candidates.Add(("vector", vectorRank.Value));
        }

        if (candidates.Count == 0)
        {
            return ("none", hybridRank);
        }

        var tiePriority = new Dictionary<string, int> { ["hybrid"] = 0, ["fts"] = 1, ["vector"] = 2 };
        var foundBy = candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => tiePriority[c.Name])
            .First().Name;
        return (foundBy, hybridRank);
    }


    private static List<CategoryAggregate> BuildCategoryAggregates(
        IReadOnlyList<BaselineQuery> queries,
        IReadOnlyList<QueryMetrics> metrics,
        RelevanceSets relevance)
    {
        var byId = metrics.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return
        [
            .. queries
                .GroupBy(q => q.Category, StringComparer.Ordinal)
                .Select(group =>
                {
                    var evaluated = group
                        .Where(q => relevance.FileLevel(q.ExpectedSource).Count > 0)
                        .Select(q => byId[q.Id])
                        .ToList();
                    return new CategoryAggregate(
                        group.Key,
                        group.Count(),
                        evaluated.Count,
                        evaluated.Count == 0 ? 0 : evaluated.Average(m => m.Ndcg5),
                        evaluated.Count == 0 ? 0 : evaluated.Average(m => m.Mrr),
                        evaluated.Count == 0 ? 0 : evaluated.Average(m => m.Recall5));
                })
                .OrderBy(c => c.Category, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Determinism hash: SHA-256 over the concatenated hybrid top-5 hash sequences
    ///     (query id + '|' + comma-joined hashes per line, queries sorted by id) — the stable
    ///     fingerprint of the report; equal across two consecutive runs when the gate holds.
    /// </summary>
    private static string DeterminismHash(IEnumerable<QueryMetrics> metrics)
    {
        var input = string.Join("\n", metrics
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => $"{m.Id}|{string.Join(",", m.HybridTop5Hashes)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private RelevanceSets BuildRelevanceSets(IReadOnlyList<BaselineQuery> queries)
    {
        var (_, fileHashes) = CorpusHashMap.Build(
            Path.Combine(_dataRoot, "memory.db"),
            queries.Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
        return new RelevanceSets(fileHashes);
    }

    private async Task<string?> ProbeProviderAsync(CancellationToken cancellationToken)
    {
        var queries = LoadQueries();
        var probe = queries.First(q => q.Id == "A1");
        var results = await TopHashesAsync(probe.Query, 0, 1, cancellationToken);
        return results.Count > 0 ? "local" : null;
    }


    private static BaselineQuery QueryById(IReadOnlyList<BaselineQuery> queries, string id) => queries.First(q => q.Id == id);

    private static string ResolveBundledDbPath()
    {
        // The docs-memory.db is copied to the output directory by the build.
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db");
        if (File.Exists(path))
        {
            return path;
        }

        // Fallback: look for it relative to the project root during development.
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Resources", "docs-memory.db"));
    }

    private static BaselineQuery[] LoadQueries()
    {
        var projectRoot = FindProjectRoot();
        var queriesPath = Path.Combine(projectRoot, "scripts", "baseline-queries.json");
        if (!File.Exists(queriesPath))
        {
            throw new InvalidOperationException($"Missing {queriesPath} — the baseline query catalog.");
        }

        return JsonSerializer.Deserialize<BaselineQuery[]>(File.ReadAllText(queriesPath), JsonOptions) ?? [];
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

    private static string CreateTempRoot() => TestData.CreateTempRoot("ai-raccoon-baseline-metrics");

    /// <summary>
    ///     Derives per-query file-level relevance sets directly from the regenerated corpus (WP4b,
    ///     docs/plans/2026-08-14-code-quality-improvement-plan.md) via <see cref="CorpusHashMap" />,
    ///     instead of the retired scripts/chunk-hash-map.json: file-level = every hash the corpus
    ///     carries for the expectedSource's file.
    /// </summary>
    private sealed class RelevanceSets(Dictionary<string, HashSet<string>> fileHashes)
    {
        public static IReadOnlySet<string> Empty { get; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> FileLevel(string? expectedSource) =>
            expectedSource is not null && fileHashes.TryGetValue(CorpusHashMap.FileKey(expectedSource), out var hashes)
                ? hashes
                : Empty;
    }


    /// <summary>JSON shape mirrors the records below, camelCase-serialized.</summary>
    public sealed record BaselineMetricsReport(
        string GeneratedAtUtc,
        int DbEntryCount,
        int DbEmbeddedCount,
        string? Provider,
        int QueryCount,
        int EvaluatedQueryCount,
        string DeterminismHash,
        IReadOnlyList<QueryMetrics> Queries,
        IReadOnlyList<CategoryAggregate> Categories);

    public sealed record QueryMetrics(
        string Id,
        string Category,
        string Query,
        IReadOnlyList<string> HybridTop5Hashes,
        IReadOnlyList<string> FtsTop5Hashes,
        IReadOnlyList<string> VectorTop5Hashes,
        [property: JsonPropertyName("nDCG5")] double Ndcg5,
        double Mrr,
        double Recall5,
        string? ExpectedFileFoundBy,
        int? ExpectedFileHybridRank);

    public sealed record CategoryAggregate(
        string Category,
        int QueryCount,
        int EvaluatedQueryCount,
        [property: JsonPropertyName("nDCG5")] double Ndcg5,
        double Mrr,
        double Recall5);

    public sealed record BaselineQuery(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        string Scope,
        int SearchLimit,
        bool NegativeTest);
}
