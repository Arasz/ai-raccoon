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
        SearchResult searchResult,
        int limit,
        double minRelativeScore = 0.0,
        double sourceLambda = 0.0,
        double consolidationThreshold = double.PositiveInfinity,
        DocScoreFormula formula = DocScoreFormula.Max)
    {
        var ranked = SourceAffinityRanker.Rank(searchResult, sourceLambda, consolidationThreshold, formula);
        return
        [
            .. ranked
                .Where(result => result.Ranking >= minRelativeScore)
                .Take(limit)
        ];
    }
}
