using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class HarnessSmokeTests
{
    /// <summary>Fixed small query set covering the doc-derived, cluster and remember judgment tiers.</summary>
    private static readonly string[] SmokeQueryIds =
    [
        "doc-badger-invariant-minimal-comments",
        "cluster-mcp",
        "doc-jsaa-doc-domain-glossary",
        "doc-jsaa-remember-recent"
    ];

    [RetryFact]
    public async Task ReferenceRunner_IsDeterministic_AndYieldsSaneResults()
    {
        var ensured = await ReferenceAssets.EnsureAsync(TestContext.Current.CancellationToken);
        ensured.Errors.ShouldBeEmpty();

        var cached = await ReferenceRunCache.GetAsync();
        var fresh = await ReferenceRunner.RunAsync(
            RealWorldCorpus.Documents, RealWorldQueries.Queries,
            cancellationToken: TestContext.Current.CancellationToken);

        cached.Engine.ShouldBe("sqlite-memory-1.3.5+sqlite-vector-1.0.0");
        cached.DocumentCount.ShouldBe(RealWorldCorpus.Documents.Count);
        cached.ResultsByQuery.Count.ShouldBe(RealWorldQueries.Queries.Count);

        foreach (var queryId in SmokeQueryIds)
        {
            var cachedResult = Query(cached, queryId);
            var freshResult = Query(fresh, queryId);

            cachedResult.Hits.Count.ShouldBeGreaterThan(0, $"{queryId} returned no hits");
            cachedResult.Hits.Count.ShouldBeLessThanOrEqualTo(cached.K);

            // Every result carries hash / ranking / path / snippet, ranked by ranking DESC.
            var previous = double.MaxValue;
            foreach (var hit in cachedResult.Hits)
            {
                hit.Hash.ShouldNotBeNullOrWhiteSpace();
                hit.Path.ShouldNotBeNullOrWhiteSpace();
                hit.Snippet.ShouldNotBeNullOrWhiteSpace();
                hit.Ranking.ShouldBeInRange(0.0, 1.0);
                hit.Ranking.ShouldBeLessThanOrEqualTo(previous);
                previous = hit.Ranking;
            }

            // Deterministic: an independent run produces the identical ranking (Kendall-tau 1).
            RetrievalMetrics.KendallTau(
                    [.. cachedResult.Hits.Select(h => h.Path)],
                    [.. freshResult.Hits.Select(h => h.Path)])
                .ShouldBe(1.0, $"{queryId} ranking differs between identical runs");

            // Sane quality: corpus queries have verified ground truth, so the reference oracle
            // must find at least one relevant doc in the top 10.
            var relevant = RealWorldQueries.Queries.Single(q => q.Id == queryId).RelevantDocIds
                .ToHashSet(StringComparer.Ordinal);
            var rankedPaths = cachedResult.Hits.Select(h => h.Path).ToList();
            RetrievalMetrics.Mrr(rankedPaths, relevant).ShouldBeGreaterThan(0.0, $"{queryId} MRR");
            RetrievalMetrics.RecallAtK(rankedPaths, relevant, 10).ShouldBeGreaterThan(0.0, $"{queryId} Recall@10");
            RetrievalMetrics.NdcgAtK(rankedPaths, relevant, 10).ShouldBeInRange(0.0, 1.0);
        }
    }

    private static ReferenceQueryResult Query(ReferenceRun run, string queryId) => run.ResultsByQuery.Single(r => r.QueryId == queryId);
}
