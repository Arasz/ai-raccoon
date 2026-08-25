using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Retrieval;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class GoldenFileTests
{
    // GoldenFile_MatchesFreshReferenceRun deleted (docs/plans/2026-08-14-code-quality-improvement-plan.md
    // WP4; docs/reviews/2026-08-14-moe-codebase-review.md RAG-F9): ReferenceRunner drives the vendored,
    // pinned sqlite-memory 1.3.5 native extension against RealWorldCorpus in its own temp db — a fixed
    // engine + a fixed corpus, entirely independent of src/AiRaccoon.Infrastructure/Sqlite. It cannot go
    // red on any AiRaccoon retrieval change, and it held a Speed=Slow CI slot on every PR for that.
    // CommittedGoldenFile_EveryHitCarriesHashRankingPathAndSnippet below is kept — it is cheap and does
    // guard the golden file's shape.

    [RetryFact]
    public void CommittedGoldenFile_EveryHitCarriesHashRankingPathAndSnippet()
    {
        var golden = GoldenFile.Load(Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName));

        golden.Queries.ShouldNotBeEmpty();
        foreach (var query in golden.Queries)
        {
            query.Id.ShouldNotBeNullOrWhiteSpace();
            foreach (var hit in query.Hits)
            {
                hit.Hash.ShouldNotBeNullOrWhiteSpace();
                hit.Path.ShouldNotBeNullOrWhiteSpace();
                hit.Snippet.ShouldNotBeNullOrWhiteSpace();
                hit.Ranking.ShouldBeInRange(0.0, 1.0);
            }
        }
    }
}
