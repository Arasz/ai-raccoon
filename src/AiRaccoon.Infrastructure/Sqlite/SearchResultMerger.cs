using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Fuses the per-context search batches with reciprocal rank fusion (spec §4.1): each
///     batch is one ranked list, contexts fuse at uniform weight, scores normalize to their
///     max (top result = 1.0). Wave 3: the fused candidates then pass through the
///     source-affinity ranker (adjacent-chunk boost, consolidation, document-first tie-break)
///     before minScore and limit apply — minScore therefore filters against the boosted-max
///     normalization (scale shifts ~10-20% at λ=0.1 for non-boosted results).
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
        var fused = ReciprocalRankFusion.Fuse(lists, rrfK, minScore: 0.0, limit: int.MaxValue);
        var ranked = SourceAffinityRanker.Rank(fused, sourceLambda, consolidationThreshold, formula);
        return ranked
            .Where(result => result.Ranking >= minScore)
            .Take(limit)
            .ToList();
    }
}
