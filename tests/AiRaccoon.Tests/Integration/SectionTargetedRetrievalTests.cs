using System.Globalization;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 6 section-targeting gates (see docs/plans/retrieval-improvement-c.md §3 Wave 6):
///     S2/S4, section hit@5, bounded file-level no-regression against the committed docs
///     corpus; limits in docs/adr/0004-dual-vector-structure-signal.md.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class SectionTargetedRetrievalTests : IDisposable
{
    private const string ProjectId = "ai-raccoon"; // matches PROJECT_ID in scripts/src/corpus_config.py
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    /// <summary>Bounded file-level no-regression tolerance; strict rank-equality is not achievable (see docs/adr/0004-dual-vector-structure-signal.md).</summary>
    private const int MaxFileRankRegression = 2;

    /// <summary>Section-target ceiling. Re-measured on the public corpus at ADR-0090; see the
    /// re-pin table in the S6a work note for the jsaa value each bound replaces.</summary>
    private const int SectionRankCeiling = 3;

    private const int FileRankCeiling = 3;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataRoot;
    private readonly string _pinnedRoot;
    private readonly SqliteConnectionFactory _factory;
    private readonly Dictionary<string, HashSet<string>> _fileHashes;
    private readonly Dictionary<string, string> _hashMap;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;
    private readonly SqliteMemoryStore _pinnedStore;

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
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        (_hashMap, _fileHashes) = LoadDerivedHashMap();

        // The S2 gate searches a second copy with pinned query vectors (ADR-0050 pattern): the
        // bundled u8s8 model puts ADR-0006 exactly on this gate's rank-3/4 boundary, so the live
        // embedding made the verdict a function of the host CPU — arm64 passes at 3, VNNI/non-VNNI
        // x64 flips to 4 (nightly #575, docs/adr/0049). The other gates keep the live path.
        _pinnedRoot = TestData.CreateTempRoot("ai-raccoon-section-targeted-pinned");
        File.Copy(bundledDb, Path.Combine(_pinnedRoot, "memory.db"));
        var pinnedFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _pinnedRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _pinnedRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _pinnedStore = TestData.CreateMemoryStore(pinnedFactory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(pinnedFactory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            PinnedQueryVectors.EmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose()
    {
        TestData.DeleteTempRoot(_dataRoot);
        TestData.DeleteTempRoot(_pinnedRoot);
    }

    /// <summary>
    ///     Section-target gates S1/S3/S4/S5/S6: the query's own section chunk must rank &lt;= 3.
    ///     Query text and expected source are read from the committed catalog by id rather than
    ///     repeated here — the previous copies drifted to name a corpus that has left the repo
    ///     (ADR-0090), and a duplicated list is one the catalog cannot keep honest.
    /// </summary>
    [RetryTheory]
    [InlineData("S1")]
    [InlineData("S3")]
    [InlineData("S4")]
    [InlineData("S5")]
    [InlineData("S6")]
    public async Task SectionTarget_RanksItsOwnSectionChunkAtMost3(string id)
    {
        var query = Catalog(id);

        var rank = await SectionRankAsync(query.Query, query.ExpectedSource!, TestContext.Current.CancellationToken);

        _output.WriteLine($"{id} section rank: {rank?.ToString() ?? "not found"} ({query.ExpectedSource})");
        rank.ShouldNotBeNull($"{id}: the '{query.ExpectedSource}' chunk must appear in the results.");
        rank.Value.ShouldBeLessThanOrEqualTo(SectionRankCeiling,
            $"{id}: the '{query.ExpectedSource}' chunk must rank <= {SectionRankCeiling} (section-targeted structure signal).");
    }

    /// <summary>
    ///     Wave 6 gate (b) + Wave 3 amendment: S2 must answer at the FILE level within the top 3.
    ///     Section-exact retrieval of the Decision chunk is a known gap, not asserted here.
    ///     Searches the pinned-vector copy (ADR-0050 pattern): with the live query embedding this
    ///     verdict was a function of the host CPU — arm64 gives rank 3, x64 flips to 4
    ///     (docs/adr/0049; nightly reds #527/#575).
    /// </summary>
    [RetryFact]
    public async Task S2_SectionQuery_AnswersAtFileLevel()
    {
        var query = Catalog("S2");
        var fileHashes = _fileHashes[CorpusHashMap.FileKey(query.ExpectedSource!)];

        var results = (await _pinnedStore.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: SearchLimit, MinRelativeScore: 0.0, RrfK: 60,
                FtsWeight: 1, VectorWeight: 1), TestContext.Current.CancellationToken)).Results
            .ToList();
        var fileRank = results.FindIndex(r => fileHashes.Contains(r.Hash)) + 1;
        var sectionRank = results.FindIndex(r => r.Hash == _hashMap[query.ExpectedSource!]) + 1;

        _output.WriteLine($"S2 file rank: {fileRank}, Decision-chunk rank: {(sectionRank == 0 ? "not found" : sectionRank.ToString())}");
        fileRank.ShouldBeGreaterThan(0, "S2: a chunk of the expected file must appear in the results.");
        fileRank.ShouldBeLessThanOrEqualTo(FileRankCeiling,
            $"S2: the expected file must answer the query within the top {FileRankCeiling}.");
    }

    /// <summary>
    ///     Wave 6 gate (docs/plans/retrieval-improvement-c.md §3 Wave 6): section-level hit@5 over the
    ///     six A-queries with section ground truth (A1–A5, A7; A6 lacks ground truth per
    ///     docs/work/2026-08-04-comparison-clean.md) must be ≥ 4/6.
    /// </summary>
    [RetryFact]
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
    [RetryFact]
    public async Task FileLevelRanks_FusedArm_NoRegressionBeyondTolerance()
    {
        var queries = FileLevelQueries();
        var contentOnly = await FileRanksAsync(queries, 1.0, TestContext.Current.CancellationToken);
        var fused = await FileRanksAsync(queries, SearchParameterSettingsKeys.DefaultStructureAlpha,
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
    [RetryFact]
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
        await _store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha,
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
        var results = (await _store.SearchAsync(new SearchQuery(
            ProjectId, text, SearchScope.Project,
            Limit: SearchLimit, MinRelativeScore: 0.0, RrfK: 60,
            FtsWeight: 1, VectorWeight: 1), cancellationToken)).Results;
        return [.. results];
    }

    /// <summary>The committed catalog entry for <paramref name="id" /> — the single source of the
    /// query text and its expected section chunk.</summary>
    private BaselineQuery Catalog(string id) =>
        LoadQueries().FirstOrDefault(q => q.Id == id)
        ?? throw new InvalidOperationException($"scripts/baseline-queries.json has no query '{id}'");

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
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db");
        if (File.Exists(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Resources", "docs-memory.db"));
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
