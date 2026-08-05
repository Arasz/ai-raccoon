using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RetrievalBaselineTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Wave 0 corpus-exclusion markers (plan C step 5; see docs/plans/retrieval-improvement-c.md §0): docs/work/, docs/state.json + .ai-badger/state.json, docs/now.md + .remember/now.md.</summary>
    /// <remarks>Provenance now lives in the source_file column, so the markers are matched
    /// against source_file — a bare value scan would false-positive on legitimate prose mentions.</remarks>
    private static readonly string[] ExcludedContentMarkers =
    [
        "docs/work/", "docs/state.json", "docs/now.md",
        ".ai-badger/state.json", ".remember/now.md",
    ];

    private const string ProjectId = "job-search-ai-assistant"; // matches PROJECT_ID in scripts/ingest-jsaa-docs.py

    private readonly string _dataRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;
    private readonly ITestOutputHelper _output;

    public RetrievalBaselineTests(ITestOutputHelper output)
    {
        _output = output;

        // The regenerated DB carries embedding.provider='local', so SearchAsync embeds the query at
        // query time and CreateGenerator throws if the bundled ONNX model is missing (ManagedHarness
        // contract). Provision it before any search.
        var ensured = BundledModel.EnsureAsync().GetAwaiter().GetResult();
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        _dataRoot = CreateTempRoot();

        // Copy the pre-built JSAA memory database into the temp data root
        var bundledDb = ResolveBundledDbPath();
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(bundledDb, dbPath);

        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(), new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task RunAllBaselineQueries_ReportsMatchStatistics()
    {
        var queries = LoadQueries();
        queries.ShouldNotBeEmpty("baseline-queries.json should contain queries");

        var hashMap = LoadChunkHashMap();
        var fileHashes = GroupByFile(hashMap);

        _output.WriteLine(
            $"Loaded {queries.Length} baseline queries, {hashMap.Count} hash-map chunks across {fileHashes.Count} files");

        var stats = await _store.GetStatsAsync(ProjectId, TestContext.Current.CancellationToken);
        _output.WriteLine($"Database has {stats.EntryCount} entries, {stats.PendingCount} pending embeds");

        var scored = new List<QueryResult>();
        var totalWithResults = 0;
        var exactMatchesAtTop3 = 0;
        var fileMatchesAtTop3 = 0;
        var expectedSourceQueryCount = queries.Count(q => !string.IsNullOrWhiteSpace(q.ExpectedSource));

        foreach (var query in queries)
        {
            var searchQuery = new SearchQuery(ProjectId, query.Query, SearchScope.Project,
                Limit: query.SearchLimit, MinScore: 0.0);
            var results = await _store.SearchAsync(searchQuery, TestContext.Current.CancellationToken);

            var (exactMatch, fileMatch, firstExactRank, firstFileRank) =
                MapResults(query, results, hashMap, fileHashes, out var mappedResults);

            if (mappedResults.Count > 0)
            {
                totalWithResults++;
            }

            if (firstExactRank is not null && firstExactRank <= 3)
            {
                exactMatchesAtTop3++;
            }

            if (firstFileRank is not null && firstFileRank <= 3)
            {
                fileMatchesAtTop3++;
            }

            if (query.ExpectedSource is not null)
            {
                _output.WriteLine(
                    $"{query.Id}: expected '{query.ExpectedSource}' " +
                    $"exact@{firstExactRank?.ToString() ?? "-"} file@{firstFileRank?.ToString() ?? "-"}");
            }

            scored.Add(new QueryResult(query.Id, query.Category, query.Query,
                query.ExpectedSource, query.ExpectedKnowledge,
                exactMatch, fileMatch,
                [.. results.Take(5).Select(r => r.Hash)],
                mappedResults));
        }

        _output.WriteLine($"Results: {totalWithResults}/{queries.Length} queries returned results");

        // At least some queries should return results from the pre-built database
        totalWithResults.ShouldBeGreaterThanOrEqualTo(1,
            "at least one query should return results from the pre-built database");

        var exactRate = expectedSourceQueryCount == 0 ? 0.0 : (double)exactMatchesAtTop3 / expectedSourceQueryCount;
        var fileRate = expectedSourceQueryCount == 0 ? 0.0 : (double)fileMatchesAtTop3 / expectedSourceQueryCount;
        _output.WriteLine($"Exact-chunk match rate @3: {exactMatchesAtTop3}/{expectedSourceQueryCount} ({exactRate:P1})");
        _output.WriteLine($"File-level match rate @3: {fileMatchesAtTop3}/{expectedSourceQueryCount} ({fileRate:P1})");

        var baseline = new BaselineReport(ProjectId, FixedNow,
            queries.Length, totalWithResults,
            expectedSourceQueryCount, exactMatchesAtTop3, fileMatchesAtTop3,
            exactRate, fileRate, scored);

        // Emit the machine-readable report first so a failing run still leaves the artifact for
        // inspection (bin-only; the temp _dataRoot is a throwaway copy).
        var binPath = Path.Combine(AppContext.BaseDirectory, "scored-baseline.json");
        await File.WriteAllTextAsync(binPath, JsonSerializer.Serialize(baseline, _jsonOptions),
            TestContext.Current.CancellationToken);
        _output.WriteLine($"Baseline written to {binPath}");

        // The report must carry the computed counts, not hardcoded placeholders (plan C step 2; see docs/plans/retrieval-improvement-c.md §0).
        baseline.ExpectedSourceMatchesAtTop3.ShouldBe(exactMatchesAtTop3);
        baseline.ExpectedSourceFileMatchesAtTop3.ShouldBe(fileMatchesAtTop3);

        // The hash-map mechanism must demonstrably work end-to-end on the committed corpus: at least
        // one query finds its exact expected chunk and at least one finds a chunk of the expected
        // file in the top 3. The >=80% success target is a later gate, not a Wave 0 gate (see docs/plans/retrieval-improvement-c.md §0).
        exactMatchesAtTop3.ShouldBeGreaterThanOrEqualTo(1,
            "at least one query should match its expected chunk (exact hash match) at rank <= 3");
        fileMatchesAtTop3.ShouldBeGreaterThanOrEqualTo(1,
            "at least one query should match a chunk of its expected file at rank <= 3");
    }

    [Fact]
    public async Task CorpusIntegrity_ExcludedContentAbsent()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        foreach (var marker in ExcludedContentMarkers)
        {
            var count = await CountSourceFileLikeAsync(connection, marker);
            count.ShouldBe(0,
                $"corpus must not contain excluded content marker '{marker}' (plan C step 5); found {count} rows");
        }
    }

    [Fact]
    public async Task CorpusIntegrity_AllEntriesEmbedded()
    {
        var stats = await _store.GetStatsAsync(ProjectId, TestContext.Current.CancellationToken);
        stats.PendingCount.ShouldBe(0,
            $"Wave 0 requires a fully embedded corpus (embed_state='embedded'); " +
            $"{stats.PendingCount} entries still pending");
        _output.WriteLine($"Corpus: {stats.EntryCount} entries, {stats.PendingCount} pending");
    }

    [Fact]
    public async Task CorpusIntegrity_ExpectedSourcesPresent()
    {
        var hashMap = LoadChunkHashMap();
        var queries = LoadQueries();
        var expected = queries.Where(q => !string.IsNullOrWhiteSpace(q.ExpectedSource)).ToArray();
        expected.Length.ShouldBeGreaterThanOrEqualTo(1,
            "baseline-queries.json should carry expectedSource entries");

        foreach (var query in expected)
        {
            hashMap.ContainsKey(query.ExpectedSource!).ShouldBeTrue(
                $"{query.Id}: expectedSource '{query.ExpectedSource}' is missing from " +
                "scripts/chunk-hash-map.json");
        }

        // The regenerated corpus stores provenance in the source_file column (plan C §3 2d;
        // see docs/plans/retrieval-improvement-c.md), so each expected source's file must appear
        // as a source_file in at least one entry (included files present).
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        foreach (var query in expected)
        {
            var count = await CountSourceFileLikeAsync(connection, SourceFileMarker(query.ExpectedSource!));
            count.ShouldBeGreaterThanOrEqualTo(1,
                $"{query.Id}: expectedSource '{query.ExpectedSource}' not found in the corpus");
        }
    }

    [Fact]
    public async Task CorpusIntegrity_HashMapMatchesDatabaseCounts()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await ReadEntryRowsAsync(connection, TestContext.Current.CancellationToken);
        var stats = await _store.GetStatsAsync(ProjectId, TestContext.Current.CancellationToken);
        var hashMap = LoadChunkHashMap();

        // The map is keyed by structured path; CLAUDE.md/HERMES.md share 10 identical section
        // chunks, so 762 keys alias to 752 distinct hashes — the count invariant is over hashes.
        var mapHashes = hashMap.Values.ToHashSet(StringComparer.Ordinal);
        mapHashes.Count.ShouldBe(rows.Count,
            "hash-map distinct hashes must equal the committed DB entry count (Wave 5a)");
        rows.Select(r => r.Hash).ToHashSet(StringComparer.Ordinal).SetEquals(mapHashes).ShouldBeTrue(
            "DB entry hashes and hash-map values must be the same set — drift means a stale map or corpus (Wave 5a)");
        stats.EntryCount.ShouldBe(rows.Count, "store-reported entry count must match the entries table");
    }

    [Fact]
    public async Task CorpusIntegrity_HashContractContentHashMatches()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await ReadEntryRowsAsync(connection, TestContext.Current.CancellationToken);

        // FR-NM-7 (see docs/work/features-native-memory/native-memory.feature) contract: stored hash == ContentHash.Of(path, value), where the committed
        // corpus's path is the hash-derived filename (SHA256(content).hex() + ".md").
        var mismatches = rows
            .Where(r => !string.Equals(ContentHash.Of(r.Path, r.Value), r.Hash, StringComparison.Ordinal))
            .Select(r => $"{r.Path}: stored {r.Hash[..12]}… != ContentHash.Of {ContentHash.Of(r.Path, r.Value)[..12]}…")
            .ToList();
        mismatches.ShouldBeEmpty("every stored entry hash must satisfy ContentHash.Of(path, value); "
            + string.Join("; ", mismatches.Take(3)));
    }

    [Fact]
    public async Task CorpusIntegrity_SourceFileAndSectionPopulated()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await ReadEntryRowsAsync(connection, TestContext.Current.CancellationToken);
        var hashMap = LoadChunkHashMap();

        rows.Count(r => string.IsNullOrWhiteSpace(r.SourceFile)).ShouldBe(0,
            "every entry must carry source_file provenance (Wave 2)");
        var withSection = rows.Count(r => !string.IsNullOrWhiteSpace(r.Section));
        _output.WriteLine(
            $"Source identity: {rows.Count} entries, {withSection} with section, "
            + $"{rows.Count - withSection} without, {rows.Select(r => r.SourceFile).Distinct().Count()} distinct files");

        // section populated ⟺ the entry's hash maps to a structured path with a '#section' part.
        var keysByHash = hashMap
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray(), StringComparer.Ordinal);
        var mismatches = rows.Count(r =>
        {
            var hasSectionKey = keysByHash.TryGetValue(r.Hash, out var keys)
                && keys.Any(k => k.Contains('#'));
            return hasSectionKey != !string.IsNullOrWhiteSpace(r.Section);
        });
        mismatches.ShouldBe(0, "section column must mirror the hash-map '#section' structured-path part");
    }

    [Fact]
    public async Task NegativeTests_H1H3_CorpusExcludesTargetContent()
    {
        var queries = LoadQueries();
        var negatives = queries.Where(q => q.NegativeTest).ToArray();
        negatives.Length.ShouldBe(3, "H1-H3 are the committed negative tests");
        negatives.Select(q => q.Id).OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["H1", "H2", "H3"]);

        // H1/H2 targets (state.json, now.md) are pinned by CorpusIntegrity_ExcludedContentAbsent;
        // H3 targets the jsaa AppHost configuration — no AppHost/program-code files may be ingested.
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await CountSourceFileLikeAsync(connection, "%apphost%")).ShouldBe(0,
            "AppHost configuration must stay out of the corpus (H3 non-evidential)");
        (await CountSourceFileLikeAsync(connection, "%.cs")).ShouldBe(0,
            "program-code files must stay out of the corpus (H3 non-evidential)");
    }

    /// <summary>'docs:adr:0011-frontend-chassis-stack.md#decision' → '0011-frontend-chassis-stack.md' (the file's tail).</summary>
    private static string SourceFileMarker(string expectedSource)
    {
        var filePart = expectedSource.Split('#')[0];
        return filePart.Split(':')[^1];
    }

    private static (bool ExactMatch, bool FileMatch, int? FirstExactRank, int? FirstFileRank) MapResults(
        BaselineQuery query, IReadOnlyList<MemorySearchResult> results,
        Dictionary<string, string> hashMap, Dictionary<string, HashSet<string>> fileHashes,
        out List<ResultEntry> mappedResults)
    {
        var exactMatch = false;
        var fileMatch = false;
        int? firstExactRank = null;
        int? firstFileRank = null;
        mappedResults = [];

        foreach (var (result, index) in results.Select((r, i) => (r, i)))
        {
            var rank = index + 1;
            var isExact = query.ExpectedSource is not null
                && hashMap.TryGetValue(query.ExpectedSource, out var expectedHash)
                && string.Equals(result.Hash, expectedHash, StringComparison.Ordinal);
            var isFile = query.ExpectedSource is not null
                && fileHashes.TryGetValue(FileKey(query.ExpectedSource), out var fileSet)
                && fileSet.Contains(result.Hash);

            if (isExact)
            {
                exactMatch = true;
                firstExactRank ??= rank;
            }

            if (isFile)
            {
                fileMatch = true;
                firstFileRank ??= rank;
            }

            mappedResults.Add(new ResultEntry(rank, result.Hash, result.Ranking, result.Path,
                result.Snippet, isExact, isFile));
        }

        return (exactMatch, fileMatch, firstExactRank, firstFileRank);
    }

    /// <summary>Groups chunk hashes by the path before '#': 'docs:adr:0011-....md#decision' → its file's hash set.</summary>
    private static Dictionary<string, HashSet<string>> GroupByFile(Dictionary<string, string> hashMap)
    {
        var fileHashes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (structuredPath, hash) in hashMap)
        {
            var fileKey = FileKey(structuredPath);
            if (!fileHashes.TryGetValue(fileKey, out var hashes))
            {
                hashes = [];
                fileHashes[fileKey] = hashes;
            }

            hashes.Add(hash);
        }

        return fileHashes;
    }

    /// <summary>'docs:adr:0011-frontend-chassis-stack.md#decision' → 'docs:adr:0011-frontend-chassis-stack.md'.</summary>
    private static string FileKey(string structuredPath) => structuredPath.Split('#')[0];

    private static async Task<int> CountSourceFileLikeAsync(SqliteConnection connection, string marker)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM entries WHERE source_file LIKE '%' || $marker || '%'";
        command.Parameters.AddWithValue("$marker", marker);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>All entry hashes/paths/values with provenance — the raw material for the Wave 5a integrity assertions (see docs/plans/retrieval-improvement-c.md §3 Wave 5a).</summary>
    private static async Task<List<EntryRow>> ReadEntryRowsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<EntryRow>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash, path, value, section, source_file FROM entries";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EntryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static Dictionary<string, string> LoadChunkHashMap()
    {
        var projectRoot = FindProjectRoot();
        var hashMapPath = Path.Combine(projectRoot, "scripts", "chunk-hash-map.json");
        if (!File.Exists(hashMapPath))
        {
            throw new InvalidOperationException(
                $"scripts/chunk-hash-map.json not found under {projectRoot}; Wave 0 requires it " +
                "committed so expected-source detection can be honest (plan C step 1).");
        }

        var json = File.ReadAllText(hashMapPath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string ResolveBundledDbPath()
    {
        // The jsaa-memory.db is copied to the output directory by the build
        var outputDir = AppContext.BaseDirectory;
        var path = Path.Combine(outputDir, "Resources", "jsaa-memory.db");
        if (File.Exists(path))
        {
            return path;
        }

        // Fallback: look for it relative to the project root during development
        var devFallback = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "Resources", "jsaa-memory.db");
        return Path.GetFullPath(devFallback);
    }

    private static BaselineQuery[] LoadQueries()
    {
        var projectRoot = FindProjectRoot();
        var queriesPath = Path.Combine(projectRoot, "scripts", "baseline-queries.json");
        if (!File.Exists(queriesPath))
        {
            return
            [
                new BaselineQuery("C1", "Invariants & Conventions", "Is TDD required?",
                    "ai-badger:invariants/tdd-mandatory.md",
                    "Yes — Write a failing, behavior-focused test before any production code change",
                    "project", 5, false)
            ];
        }

        var json = File.ReadAllText(queriesPath);
        return JsonSerializer.Deserialize<BaselineQuery[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
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

    private static string CreateTempRoot() => TestData.CreateTempRoot("ai-raccoon-tests");

    public sealed record BaselineQuery(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        string Scope,
        int SearchLimit,
        bool NegativeTest);

    public sealed record QueryResult(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        bool IsExpectedSource,
        bool IsExpectedSourceFile,
        IReadOnlyList<string> Top5Hashes,
        IReadOnlyList<ResultEntry> Results);

    public sealed record ResultEntry(
        int Rank,
        string Hash,
        double Ranking,
        string Path,
        string Snippet,
        bool IsExpectedSource,
        bool IsExpectedSourceFile);

    /// <summary>One committed-corpus row: identity, content, and provenance (Wave 5a integrity assertions; see docs/plans/retrieval-improvement-c.md §3 Wave 5a).</summary>
    public sealed record EntryRow(
        string Hash,
        string Path,
        string Value,
        string? Section,
        string? SourceFile);

    public sealed record BaselineReport(
        string ProjectId,
        DateTimeOffset ExportedAt,
        int QueryCount,
        int QueriesWithResults,
        int ExpectedSourceQueryCount,
        int ExpectedSourceMatchesAtTop3,
        int ExpectedSourceFileMatchesAtTop3,
        double ExpectedSourceMatchRateAtTop3,
        double ExpectedSourceFileMatchRateAtTop3,
        IReadOnlyList<QueryResult> QueryResults);
}
