using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Fuses per-context batches with RRF, then applies source-affinity ranking (see
///     docs/adr/0005-source-affinity-ranking.md) before minRelativeScore and limit. The floor is
///     relative to the boosted-max normalization, never absolute (docs/adr/0047-relative-score-floor.md).
/// </summary>
internal static class SearchResultMerger
{
    public static IReadOnlyList<MemorySearchResult> Merge(
        IReadOnlyList<MemorySearchResult> searchResults,
        SearchQuery searchQuery,
        SearchParameters parameters,
        FtsQueryPlan queryPlan) =>
        Merge(searchResults, searchQuery.Limit, searchQuery.MinRelativeScore, parameters.RrfK, parameters.SourceLambdaFor(queryPlan), parameters.ConsolidationThreshold,
            parameters.DocScoreFormula);

    public static IReadOnlyList<MemorySearchResult> Merge(
        IReadOnlyList<MemorySearchResult> searchResults,
        int limit,
        double minRelativeScore = 0.0,
        int rrfK = SearchQuery.DefaultRrfK,
        double sourceLambda = 0.0,
        double consolidationThreshold = double.PositiveInfinity,
        DocScoreFormula formula = DocScoreFormula.Max)
    {
        var unitWeightResults = new WeightedResults(searchResults, 1.0);
        var fused = ReciprocalRankFusion.Fuse([unitWeightResults], rrfK, 0.0, int.MaxValue);
        var ranked = SourceAffinityRanker.Rank(fused, sourceLambda, consolidationThreshold, formula);
        return
        [
            .. ranked
                .Where(result => result.Ranking >= minRelativeScore)
                .Take(limit)
        ];
    }
}
