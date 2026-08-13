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
///     Wave 2 gates of docs/plans/retrieval-improvement-c.md: search results carry source
///     identity, FTS-only queries resolve via the source column, and invariants C1/C2/C5
///     hold their pinned hybrid ranks.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SourceIdentityTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant"; // matches PROJECT_ID in scripts/ingest-jsaa-docs.py

    private const string Adr0011Decision = "docs:adr:0011-frontend-chassis-stack.md#decision";
    private const string Adr0011Source = "docs/adr/0011-frontend-chassis-stack.md";
    private const string Adr0070Decision = "docs:adr:0070-documentation-structure-and-trust-model.md#decision";
    private const string Adr0070Source = "docs/adr/0070-documentation-structure-and-trust-model.md";
    private const string InvariantTdd = "ai-badger:invariants/tdd-mandatory.md";
    private const string InvariantScreaming = "ai-badger:invariants/screaming-architecture.md";
    private const string InvariantSecrets = "ai-badger:invariants/no-hardcoded-secrets.md";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

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
        File.Copy(ResolveBundledDbPath(), Path.Combine(_dataRoot, "memory.db"));

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task SearchResults_CarrySourceIdentity_ForIngestedChunks()
    {
        var hashMap = LoadChunkHashMap();
        var expectedHash = hashMap[Adr0011Decision];

        var results = await _store.SearchAsync(new SearchQuery(ProjectId,
            "Why was shadcn/ui chosen over gluestack.io?",
            SearchScope.Project, Limit: 10, MinScore: 0.0), TestContext.Current.CancellationToken);

        var hit = results.FirstOrDefault(r => r.Hash == expectedHash);
        hit.ShouldNotBeNull("the ADR-0011 decision chunk must appear in the top 10");
        hit.SourceFile.ShouldBe(Adr0011Source,
            "the result must carry the original relative path of its source file");
        hit.TotalChunks.ShouldBeGreaterThanOrEqualTo(2, "ADR-0011 is chunked into several sections");
        hit.ChunkIndex.ShouldBeInRange(0, hit.TotalChunks - 1, "ChunkIndex is 0-based within the source");

        foreach (var result in results.Where(r => r.SourceFile is not null))
        {
            result.TotalChunks.ShouldBeGreaterThanOrEqualTo(1);
            result.ChunkIndex.ShouldBeInRange(0, result.TotalChunks - 1);
        }
    }

    /// <summary>S2 (docs/plans/retrieval-improvement-c.md §3 Wave 2): the Decision-chunk's own rank is logged, not asserted — Wave 6's dual-vector structure signal is the target for that.</summary>
    [Fact]
    public async Task S2_WhatDoesAdr0011Decide_FindsAdr0011WithinTop3_AndLogsDecisionChunkRank()
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId, "What does ADR-0011 decide?",
            SearchScope.Project, Limit: 20, MinScore: 0.0), TestContext.Current.CancellationToken);

        var (fileHit, fileRank) = FindRank(results, r => string.Equals(r.SourceFile, Adr0011Source, StringComparison.Ordinal));
        fileHit.ShouldNotBeNull("S2: a chunk of ADR-0011 must appear in the top 20");
        fileRank.ShouldBeInRange(1, 3, "S2 gate: ADR-0011 at rank <= 3 (source-column fix)");

        var (decisionHit, decisionRank) = FindRank(results, r => r.Hash == hashMap[Adr0011Decision]);
        _output.WriteLine($"S2: Decision-section chunk at hybrid rank {(decisionHit is null ? "not found" : decisionRank.ToString())} (Wave 6 target: <= 3)");
    }

    /// <summary>Q2 (docs/plans/retrieval-improvement-c.md §3 Wave 2): the decision chunk's rank is logged, not asserted — the header chunk legitimately outranks it for a bare identifier.</summary>
    [Fact]
    public async Task Q2_IdentifierOnly_Adr0070_FtsOnlyFileRankWithinTop3()
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId, "ADR-0070",
                SearchScope.Project, Limit: 10, MinScore: 0.0, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken);

        var (fileHit, fileRank) = FindRank(results, r => string.Equals(r.SourceFile, Adr0070Source, StringComparison.Ordinal));
        fileHit.ShouldNotBeNull("Q2: a chunk of ADR-0070 must appear in the FTS-only top 10");
        fileRank.ShouldBeInRange(1, 3, "Q2 gate: ADR-0070 at FTS-only rank <= 3 (source-column fix)");

        var (decisionHit, decisionRank) = FindRank(results, r => r.Hash == hashMap[Adr0070Decision]);
        _output.WriteLine($"Q2: Decision-section chunk at FTS-only rank {(decisionHit is null ? "not found" : decisionRank.ToString())}");
    }

    [Fact]
    public async Task SourcePathQuery_ReturnsTheExactChunk()
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId,
            "docs/adr/0011-frontend-chassis-stack.md#decision",
            SearchScope.Project, Limit: 5, MinScore: 0.0), TestContext.Current.CancellationToken);

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[Adr0011Decision]);
        hit.ShouldNotBeNull("a source-path query must return its exact chunk");
        rank.ShouldBe(1, "the exact chunk of the queried source path ranks first");
    }

    /// <summary>
    ///     Gates re-pinned to the jsaa corpus (docs/work/archive/2026-08-06-baseline-repin-new-corpus.md):
    ///     C1 holds hybrid rank 1; C5's floor is 5 (ADR content on secrets/config now outranks
    ///     it). The pins are the measured no-regression floor, not an aspiration.
    /// </summary>
    [Theory]
    [InlineData("Is TDD required?", InvariantTdd, 1)]
    [InlineData("Are hardcoded secrets allowed?", InvariantSecrets, 5)]
    public async Task InvariantQueries_C1C5_HoldMeasuredHybridRanks(string query, string expectedSource,
        int expectedRank)
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId, query,
            SearchScope.Project, Limit: 5, MinScore: 0.0), TestContext.Current.CancellationToken);

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
    [Fact]
    public async Task InvariantC2_ScreamingArchitecture_FtsOnlyRank1()
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId,
                "What is the screaming architecture rule?",
                SearchScope.Project, Limit: 5, MinScore: 0.0, FtsWeight: 1, VectorWeight: 0),
            TestContext.Current.CancellationToken);

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[InvariantScreaming]);
        hit.ShouldNotBeNull("the screaming-architecture invariant must appear in the FTS-only top 5");
        rank.ShouldBe(1, "the invariant stays at FTS-only rank 1 (no regression)");
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

    private static Dictionary<string, string> LoadChunkHashMap()
    {
        var projectRoot = FindProjectRoot();
        var json = File.ReadAllText(Path.Combine(projectRoot, "scripts", "chunk-hash-map.json"));
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
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
}
