using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Fuses per-context batches with RRF, then applies source-affinity ranking (see
///     docs/adr/0005-source-affinity-ranking.md) before minScore and limit — minScore filters against the boosted-max
///     normalization.
/// </summary>
internal static class SearchResultMerger
{
    public static IReadOnlyList<MemorySearchResult> Merge(
        IEnumerable<IReadOnlyList<MemorySearchResult>> batches,
        int limit,
        double minScore = 0.0,
        int rrfK = SearchQuery.DefaultRrfK,
        double sourceLambda = 0.0,
        double consolidationThreshold = double.PositiveInfinity,
        DocScoreFormula formula = DocScoreFormula.Max)
    {
        ArgumentNullException.ThrowIfNull(batches);

        var lists = batches
            .Select(batch => (batch, Weight: 1.0))
            .ToList();
        var fused = ReciprocalRankFusion.Fuse(lists, rrfK, 0.0, int.MaxValue);
        var ranked = SourceAffinityRanker.Rank(fused, sourceLambda, consolidationThreshold, formula);
        return
        [
            .. ranked
                .Where(result => result.Ranking >= minScore)
                .Take(limit)
        ];
    }
}
