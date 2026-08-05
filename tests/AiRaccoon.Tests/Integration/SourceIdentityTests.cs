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
///     Wave 2 gates of docs/plans/retrieval-improvement-c.md: search results carry source
///     identity (SourceFile/ChunkIndex/TotalChunks), the FTS source column answers identifier
///     queries without the vector modality, source-path queries land on the exact chunk, and
///     invariants C1/C2/C5 stay at rank 1.
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
    private readonly SqliteMemoryStore _store;
    private readonly ITestOutputHelper _output;

    public SourceIdentityTests(ITestOutputHelper output)
    {
        _output = output;

        // The regenerated DB carries embedding.provider='local', so SearchAsync embeds the
        // query at query time; provision the bundled ONNX model before any search.
        var ensured = BundledModel.EnsureAsync().GetAwaiter().GetResult();
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        _dataRoot = TestData.CreateTempRoot("ai-raccoon-source-identity");
        File.Copy(ResolveBundledDbPath(), Path.Combine(_dataRoot, "memory.db"));

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(), new EmbeddingService());
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

        // Every sourced result carries a consistent identity.
        foreach (var result in results.Where(r => r.SourceFile is not null))
        {
            result.TotalChunks.ShouldBeGreaterThanOrEqualTo(1);
            result.ChunkIndex.ShouldBeInRange(0, result.TotalChunks - 1);
        }
    }

    /// <summary>
    ///     S2 (Wave 2 gate; see docs/plans/retrieval-improvement-c.md §3 Wave 2): the ADR-0011 file must rank within the top 3. The
    ///     Decision-section chunk itself is logged — the FTS cannot bridge 'decide' to the
    ///     '## Decision' heading (no stemming) and bm25's document-length normalization
    ///     crushes the 13.8 KB decision chunk; the section-level &lt;=3 target is Wave 6's
    ///     dual-vector structure signal (measured hybrid rank: see output).
    /// </summary>
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
        _output.WriteLine($"S2: Decision-section chunk at hybrid rank " +
                          $"{(decisionHit is null ? "not found" : decisionRank.ToString())} (Wave 6 target: <= 3)");
    }

    /// <summary>
    ///     Q2 (Wave 2 gate; see docs/plans/retrieval-improvement-c.md §3 Wave 2): 'ADR-0070' identifier-only returns ADR-0070's file at
    ///     FTS-only rank &lt;=3 — the source-column fix works without the vector modality. The
    ///     decision chunk's exact rank is logged (the header chunk legitimately outranks it
    ///     for a bare identifier — it carries the ADR's title).
    /// </summary>
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
        _output.WriteLine($"Q2: Decision-section chunk at FTS-only rank " +
                          $"{(decisionHit is null ? "not found" : decisionRank.ToString())}");
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

    [Theory]
    [InlineData("Is TDD required?", InvariantTdd)]
    [InlineData("Are hardcoded secrets allowed?", InvariantSecrets)]
    public async Task InvariantQueries_C1C5_StayAtHybridRank1(string query, string expectedSource)
    {
        var hashMap = LoadChunkHashMap();

        var results = await _store.SearchAsync(new SearchQuery(ProjectId, query,
            SearchScope.Project, Limit: 5, MinScore: 0.0), TestContext.Current.CancellationToken);

        var (hit, rank) = FindRank(results, r => r.Hash == hashMap[expectedSource]);
        hit.ShouldNotBeNull($"{expectedSource} must appear in the top 5");
        rank.ShouldBe(1, $"invariant {expectedSource} must stay at rank 1 (no regression)");
    }

    /// <summary>
    ///     C2's hybrid rank collapsed with clean content (2d; see docs/plans/retrieval-improvement-c.md §3 2d):
    ///     its vector rank is &gt;100, so the RRF fusion (k=60) sinks a perfect FTS rank 1 — the
    ///     Wave 0 hybrid rank 1 was an artifact of the embedded provenance prefix. The source
    ///     column holds the invariant at FTS-only rank 1; the fusion weighting is Wave 4's sweep
    ///     (see docs/adr/0006-rrf-parameter-optimization.md).
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
