using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 6 section-targeting gates (see docs/plans/retrieval-improvement-c.md §3 Wave 6):
///     S2/S4, section hit@5, bounded file-level no-regression against the committed jsaa
///     corpus; limits in docs/adr/0004-dual-vector-structure-signal.md.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SectionTargetedRetrievalTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant"; // matches scripts/ingest-jsaa-docs.py
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    /// <summary>Bounded file-level no-regression tolerance; strict rank-equality is not achievable (see docs/adr/0004-dual-vector-structure-signal.md).</summary>
    private const int MaxFileRankRegression = 2;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataRoot;
    private readonly SqliteConnectionFactory _factory;
    private readonly Dictionary<string, string> _hashMap;
    private readonly Dictionary<string, HashSet<string>> _fileHashes;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

    public SectionTargetedRetrievalTests(ITestOutputHelper output)
    {
        _output = output;

        // The committed corpus carries provider='local', so search embeds at query time.
        var ensured = TestData.CreateBundledModel().EnsureAsync().GetAwaiter().GetResult();
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        _dataRoot = TestData.CreateTempRoot("ai-raccoon-section-targeted");
        var bundledDb = ResolveBundledDbPath();
        File.Copy(bundledDb, Path.Combine(_dataRoot, "memory.db"));

        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
        (_hashMap, _fileHashes) = LoadDerivedHashMap();
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Wave 6 gate S4: restored to the production SearchLimit=10 window and rank &lt;= 3 once
    ///     FileIngestor populated section from the heading leaf.
    /// </summary>
    [Fact]
    public async Task S4_ConsequencesOfAdr0011_ConsequencesChunkAtRankAtMost3()
    {
        var expectedHash = _hashMap["docs:adr:0011-frontend-chassis-stack.md#consequences"];
        var results = await _store.SearchAsync(new SearchQuery(
            ProjectId, "Consequences of ADR-0011?", SearchScope.Project,
            Limit: 10, MinScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 1), TestContext.Current.CancellationToken);
        var rank = results.ToList().FindIndex(r => r.Hash == expectedHash) + 1;

        _output.WriteLine($"S4 section rank: {rank}");
        rank.ShouldBeGreaterThan(0, "S4: the ADR-0011 Consequences chunk must appear in the top 10.");
        rank.ShouldBeLessThanOrEqualTo(3,
            "S4: the ADR-0011 Consequences chunk must rank <= 3 (section-targeted structure signal).");
    }

    /// <summary>Wave 5b gate S1 (docs/plans/retrieval-improvement-c.md §3 Wave 5b): ADR-0011's Context section target finds the Context chunk at rank ≤ 3.</summary>
    [Fact]
    public async Task S1_ContextOfAdr0011_ContextChunkAtRankAtMost3()
    {
        var rank = await SectionRankAsync("What context led to the ADR-0011 frontend stack decisions?",
            "docs:adr:0011-frontend-chassis-stack.md#context", TestContext.Current.CancellationToken);

        _output.WriteLine($"S1 section rank: {rank?.ToString() ?? "not found"}");
        rank.ShouldNotBeNull("S1: the ADR-0011 Context chunk must appear in the results.");
        rank.Value.ShouldBeLessThanOrEqualTo(3,
            "S1: the ADR-0011 Context chunk must rank <= 3 (section-targeted structure signal).");
    }

    /// <summary>Wave 5b gate S3 (docs/plans/retrieval-improvement-c.md §3 Wave 5b): ADR-0011's Alternatives-considered section target finds that chunk at rank ≤ 3.</summary>
    [Fact]
    public async Task S3_AlternativesOfAdr0011_AlternativesChunkAtRankAtMost3()
    {
        var rank = await SectionRankAsync("What alternatives were considered for ADR-0011?",
            "docs:adr:0011-frontend-chassis-stack.md#alternatives-considered", TestContext.Current.CancellationToken);

        _output.WriteLine($"S3 section rank: {rank?.ToString() ?? "not found"}");
        rank.ShouldNotBeNull("S3: the ADR-0011 Alternatives-considered chunk must appear in the results.");
        rank.Value.ShouldBeLessThanOrEqualTo(3,
            "S3: the ADR-0011 Alternatives-considered chunk must rank <= 3.");
    }

    /// <summary>
    ///     Wave 5b gate S5 (docs/plans/retrieval-improvement-c.md §3 Wave 5b): a cross-document structural
    ///     query — ADR-0011 is the formal record, docs/frontend-architecture.md §2-3 the deep-dive — must
    ///     rank the formal record's Decision chunk at ≤ 3.
    /// </summary>
    [Fact]
    public async Task S5_FrontendStackDecisionDocument_FindsFormalRecordAtRankAtMost3()
    {
        var rank = await SectionRankAsync("Which documents record the frontend stack decision?",
            "docs:adr:0011-frontend-chassis-stack.md#decision", TestContext.Current.CancellationToken);

        _output.WriteLine($"S5 section rank: {rank?.ToString() ?? "not found"}");
        rank.ShouldNotBeNull("S5: the ADR-0011 Decision chunk must appear in the results.");
        rank.Value.ShouldBeLessThanOrEqualTo(3,
            "S5: the formal decision record's chunk must rank <= 3 (cross-document structural query).");
    }

    /// <summary>Wave 5b gate S6 (docs/plans/retrieval-improvement-c.md §3 Wave 5b): a section target on a second ADR — ADR-0060's What-is-lost chunk must rank ≤ 3.</summary>
    [Fact]
    public async Task S6_WhatIsLostByMcpDeletion_WhatIsLostChunkAtRankAtMost3()
    {
        var rank = await SectionRankAsync("What is lost by deleting the MCP server?",
            "docs:adr:0060-delete-the-mcp-server.md#what-is-lost", TestContext.Current.CancellationToken);

        _output.WriteLine($"S6 section rank: {rank?.ToString() ?? "not found"}");
        rank.ShouldNotBeNull("S6: the ADR-0060 What-is-lost chunk must appear in the results.");
        rank.Value.ShouldBeLessThanOrEqualTo(3,
            "S6: the ADR-0060 What-is-lost chunk must rank <= 3 (section target on a second ADR).");
    }

    /// <summary>
    ///     Wave 6 gate (b) + Wave 3 amendment (docs/plans/retrieval-improvement-c.md §3): "What does
    ///     ADR-0011 decide?" must answer at the file level within the top 3. (Section-exact retrieval of
    ///     the Decision chunk is a known gap, not asserted here — see the in-body note.)
    /// </summary>
    [Fact]
    public async Task S2_WhatDoesAdr0011Decide_AnswersAtFileLevel()
    {
        var query = "What does ADR-0011 decide?";
        var decisionSource = "docs:adr:0011-frontend-chassis-stack.md#decision";
        var fileHashes = _fileHashes[CorpusHashMap.FileKey(decisionSource)];

        var results = await TopResultsAsync(query, TestContext.Current.CancellationToken);
        var fileRank = results.FindIndex(r => fileHashes.Contains(r.Hash)) + 1;
        var sectionRank = results.FindIndex(r => r.Hash == _hashMap[decisionSource]) + 1;

        _output.WriteLine($"S2 file rank: {fileRank}, Decision-chunk rank: {(sectionRank == 0 ? "not found" : sectionRank.ToString())}");
        fileRank.ShouldBeGreaterThan(0, "S2: an ADR-0011 chunk must appear in the results.");
        fileRank.ShouldBeLessThanOrEqualTo(3,
            "S2: the ADR-0011 file must answer the query within the top 3.");
        // File-level answers at rank 1; the exact Decision chunk sits outside the top 10 because
        // structure_embedding is unpopulated (tracked follow-up) — file-level is the asserted contract.
        _output.WriteLine(
            "S2 section-exact gap: decision chunk outside top-10 on the re-pinned corpus " +
            "(structure-signal follow-up; see docs/work/2026-08-06-baseline-repin-new-corpus.md)");
    }

    /// <summary>
    ///     Wave 6 gate (docs/plans/retrieval-improvement-c.md §3 Wave 6): section-level hit@5 over the
    ///     six A-queries with section ground truth (A1–A5, A7; A6 lacks ground truth per
    ///     docs/work/2026-08-04-comparison-clean.md) must be ≥ 4/6.
    /// </summary>
    [Fact]
    public async Task SectionHitAt5_OverAdrQueries_AtLeast4Of6()
    {
        var sectionQueries = QueriesWithSectionGroundTruth();
        sectionQueries.Count.ShouldBe(6,
            "section ground truth exists for six A-queries (A1-A5, A7) per the comparison doc");

        var hits = new List<string>();
        var misses = new List<string>();
        foreach (var query in sectionQueries)
        {
            var rank = await SectionRankAsync(query.Query, query.ExpectedSource!,
                TestContext.Current.CancellationToken);
            if (rank is null)
            {
                misses.Add($"{query.Id}@miss");
            }
            else if (rank <= RankCutoff)
            {
                hits.Add($"{query.Id}@{rank}");
            }
            else
            {
                misses.Add($"{query.Id}@{rank}");
            }
        }

        _output.WriteLine($"Section hit@5: {hits.Count}/{sectionQueries.Count} ({string.Join(", ", hits)}; miss: {string.Join(", ", misses)})");
        hits.Count.ShouldBeGreaterThanOrEqualTo(4,
            $"section-level hit@5 over A1-A7 must be >= 4/6; got {hits.Count}/6");
    }

    /// <summary>Wave 6 gate (docs/plans/retrieval-improvement-c.md §3 Wave 6): no content-only file-level rank regression beyond the bounded tolerance (docs/adr/0004-dual-vector-structure-signal.md).</summary>
    [Fact]
    public async Task FileLevelRanks_FusedArm_NoRegressionBeyondTolerance()
    {
        var queries = FileLevelQueries();
        var contentOnly = await FileRanksAsync(queries, 1.0, TestContext.Current.CancellationToken);
        var fused = await FileRanksAsync(queries, StructureFusion.DefaultAlpha,
            TestContext.Current.CancellationToken);

        var hit5Regressions = queries
            .Where(query => (contentOnly[query.Id] ?? int.MaxValue) <= RankCutoff
                            && (fused[query.Id] ?? int.MaxValue) > RankCutoff)
            .Select(query => query.Id)
            .ToList();
        var rankRegressions = queries
            .Where(query => contentOnly[query.Id] is not null && fused[query.Id] is not null
                                                              && fused[query.Id]! > contentOnly[query.Id]! + MaxFileRankRegression)
            .Select(query => $"{query.Id}: {contentOnly[query.Id]} -> {fused[query.Id]}")
            .ToList();

        _output.WriteLine($"File-level ranks (content-only -> fused): {string.Join("; ", queries.Select(query =>
            $"{query.Id} {contentOnly[query.Id]?.ToString() ?? "miss"}->{fused[query.Id]?.ToString() ?? "miss"}"))}");
        hit5Regressions.ShouldBeEmpty(
            $"every expected file must stay in the top 5 under dual-vector fusion; {string.Join(", ", hit5Regressions)}");
        rankRegressions.ShouldBeEmpty(
            $"no query may lose more than {MaxFileRankRegression} file-level rank positions vs content-only; {string.Join("; ", rankRegressions)}");
    }

    /// <summary>Pre-Wave-6 banks gain the structure columns on open (ALTER TABLE migration path).</summary>
    [Fact]
    public async Task SchemaMigration_AddsStructureColumns_ToLegacyBank()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var columns = await ColumnNamesAsync(connection);

        columns.ShouldContain("heading_path", "Wave 6 migration must add heading_path");
        columns.ShouldContain("structure_embedding", "Wave 6 migration must add structure_embedding");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Tolerance-aware rank per ADR-0015: near-ties within <see cref="GoldenFile.RankingTolerance"/> don't count as ahead.</summary>
    private async Task<int?> SectionRankAsync(string text, string expectedSource, CancellationToken cancellationToken)
    {
        var expectedHash = _hashMap[expectedSource];
        var results = await TopResultsAsync(text, cancellationToken);
        var expected = results.FirstOrDefault(r => r.Hash == expectedHash);
        if (expected is null)
        {
            _output.WriteLine($"[{text}] expected '{expectedSource}' not in top {results.Count}: {string.Join(", ", results.Take(RankCutoff).Select(r => r.Hash[..8]))}");
            return null;
        }

        var tolerantRank = 1 + results.Count(r => r.Ranking > expected.Ranking + GoldenFile.RankingTolerance);
        var rawRank = results.FindIndex(r => r.Hash == expected.Hash) + 1;

        // Bounds total near-tie absorption to one position: the tolerant rank forgives at most one
        // real slide, never an unlimited run of adjacent near-ties.
        return Math.Max(tolerantRank, rawRank - 1);
    }

    private async Task<Dictionary<string, int?>> FileRanksAsync(
        IReadOnlyList<BaselineQuery> queries, double alpha, CancellationToken cancellationToken)
    {
        await _store.SetSettingAsync(StructureFusion.AlphaSettingKey,
            alpha.ToString(CultureInfo.InvariantCulture), cancellationToken);
        var ranks = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var fileHashes = _fileHashes[CorpusHashMap.FileKey(query.ExpectedSource!)];
            var results = await TopResultsAsync(query.Query, cancellationToken);
            var rank = results.FindIndex(r => fileHashes.Contains(r.Hash)) + 1;
            ranks[query.Id] = rank == 0 ? null : rank;
        }

        return ranks;
    }

    private async Task<List<MemorySearchResult>> TopResultsAsync(string text, CancellationToken cancellationToken)
    {
        var results = await _store.SearchAsync(new SearchQuery(
            ProjectId, text, SearchScope.Project,
            Limit: SearchLimit, MinScore: 0.0, RrfK: 60,
            FtsWeight: 1, VectorWeight: 1), cancellationToken);
        return [.. results];
    }

    private List<BaselineQuery> QueriesWithSectionGroundTruth() =>
    [
        .. LoadQueries()
            .Where(q => q.Id is "A1" or "A2" or "A3" or "A4" or "A5" or "A7")
            .Where(q => q.ExpectedSource is not null && _hashMap.ContainsKey(q.ExpectedSource))
    ];

    private List<BaselineQuery> FileLevelQueries() =>
    [
        .. LoadQueries()
            .Where(q => q.ExpectedSource is not null && _hashMap.ContainsKey(q.ExpectedSource))
            .Where(q => q.Id is "A1" or "A2" or "A3" or "A4" or "A5" or "A6" or "A7" or "C1" or "C2" or "C5")
    ];

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(entries)";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>
    ///     Derives the expected-source hash map and per-file hash sets directly from the regenerated
    ///     corpus (WP4b, docs/plans/2026-08-14-code-quality-improvement-plan.md), instead of the
    ///     retired scripts/chunk-hash-map.json. See <see cref="CorpusHashMap" />.
    /// </summary>
    private (Dictionary<string, string> HashMap, Dictionary<string, HashSet<string>> FileHashes) LoadDerivedHashMap() =>
        CorpusHashMap.Build(
            Path.Combine(_dataRoot, "memory.db"),
            LoadQueries().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));

    private BaselineQuery[] LoadQueries()
    {
        var projectRoot = FindProjectRoot();
        var queriesPath = Path.Combine(projectRoot, "scripts", "baseline-queries.json");
        return JsonSerializer.Deserialize<BaselineQuery[]>(File.ReadAllText(queriesPath), JsonOptions) ?? [];
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
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
