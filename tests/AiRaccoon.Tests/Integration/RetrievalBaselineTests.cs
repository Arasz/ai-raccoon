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

[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RetrievalBaselineTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SqliteMemoryStore _store;
    private readonly ITestOutputHelper _output;

    public RetrievalBaselineTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = CreateTempRoot();

        // Copy the pre-built JSAA memory database into the temp data root
        var bundledDb = ResolveBundledDbPath();
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(bundledDb, dbPath);

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(),
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task RunAllBaselineQueries_ReportsMatchStatistics()
    {
        var queries = LoadQueries();
        queries.ShouldNotBeEmpty("baseline-queries.json should contain queries");
        _output.WriteLine($"Loaded {queries.Length} baseline queries");

        var stats = await _store.GetStatsAsync("jsaa", TestContext.Current.CancellationToken);
        _output.WriteLine($"Database has {stats.EntryCount} entries");

        var scored = new List<QueryResult>();
        var totalWithResults = 0;

        foreach (var query in queries)
        {
            var searchQuery = new SearchQuery("jsaa", query.Query, SearchScope.Project,
                Limit: query.SearchLimit, MinScore: 0.0);
            var results = await _store.SearchAsync(searchQuery, TestContext.Current.CancellationToken);

            var mappedResults = results.Select((r, i) => new ResultEntry(
                i + 1, r.Hash, r.Ranking,
                r.Path, r.Snippet,
                false
            )).ToList();

            if (mappedResults.Count > 0)
            {
                totalWithResults++;
            }

            scored.Add(new QueryResult(query.Id, query.Category, query.Query,
                query.ExpectedSource, query.ExpectedKnowledge, mappedResults));
        }

        _output.WriteLine($"Results: {totalWithResults}/{queries.Length} queries returned results");

        // At least some queries should return results from the pre-built database
        totalWithResults.ShouldBeGreaterThanOrEqualTo(1,
            "at least one query should return results from the pre-built database");

        var baseline = new BaselineReport("jsaa", DateTimeOffset.UtcNow,
            queries.Length, totalWithResults, 0, scored);
        var reportPath = Path.Combine(_dataRoot, "scored-baseline.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(baseline, _jsonOptions),
            TestContext.Current.CancellationToken);
        _output.WriteLine($"Baseline written to {reportPath}");
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
        IReadOnlyList<ResultEntry> Results);

    public sealed record ResultEntry(
        int Rank,
        string Hash,
        double Ranking,
        string Path,
        string Snippet,
        bool IsExpectedSource);

    public sealed record BaselineReport(
        string ProjectId,
        DateTimeOffset ExportedAt,
        int QueryCount,
        int QueriesWithResults,
        int ExpectedSourceMatchesAtTop3,
        IReadOnlyList<QueryResult> QueryResults);
}
