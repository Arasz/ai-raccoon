using AiRaccoon.Benchmarks.SearchFixture;
using AiRaccoon.Core.Memory;
using BenchmarkDotNet.Attributes;

namespace AiRaccoon.Benchmarks.Benchmarks;

/// <summary>
///     End-to-end <see cref="AiRaccoon.Infrastructure.Sqlite.SqliteMemoryStore.SearchAsync" />
///     latency over the WP4 fixture bank (docs/plans/2026-08-08-search-knn-perf.md §5): the
///     headline hybrid case plus per-modality variants (FtsWeight=0 / VectorWeight=0) so a
///     regression can be attributed to keyword ranking, vector KNN, or fusion/snippet
///     resolution. Measures the full call — ranking and deferred-snippet resolution included,
///     not just the SQL.
/// </summary>
[MemoryDiagnoser]
public class SearchLatencyBenchmark
{
    private SearchFixtureBank _bank = null!;

    /// <summary>The fixed 10-query set: BenchmarkDotNet runs every benchmark once per query, so results are per-query stable rather than averaged over a round-robin.</summary>
    public static IEnumerable<string> Queries => SearchFixtureBank.Queries;

    [ParamsSource(nameof(Queries))] public string Query { get; set; } = "";

    [GlobalSetup]
    public async Task Setup() => _bank = await SearchFixtureBank.BuildAsync();

    [GlobalCleanup]
    public async Task Cleanup() => await _bank.DisposeAsync();

    /// <summary>Headline case: hybrid FTS+vector search, scope=all, limit=10.</summary>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<MemorySearchResult>> SearchAll_Limit10() =>
        _bank.Store.SearchAsync(new SearchQuery(SearchFixtureBank.ProjectId, Query,
            Limit: 10, MinRelativeScore: 0.0));

    /// <summary>Vector-only (FtsWeight=0): isolates vec0 KNN + fusion + deferred-snippet cost.</summary>
    [Benchmark]
    public Task<IReadOnlyList<MemorySearchResult>> SearchAll_VectorOnly_Limit10() =>
        _bank.Store.SearchAsync(new SearchQuery(SearchFixtureBank.ProjectId, Query,
            Limit: 10, MinRelativeScore: 0.0, FtsWeight: 0, VectorWeight: 1));

    /// <summary>FTS-only (VectorWeight=0): isolates the FTS5 bm25 + snippet() cost.</summary>
    [Benchmark]
    public Task<IReadOnlyList<MemorySearchResult>> SearchAll_FtsOnly_Limit10() =>
        _bank.Store.SearchAsync(new SearchQuery(SearchFixtureBank.ProjectId, Query,
            Limit: 10, MinRelativeScore: 0.0, FtsWeight: 1, VectorWeight: 0));
}
