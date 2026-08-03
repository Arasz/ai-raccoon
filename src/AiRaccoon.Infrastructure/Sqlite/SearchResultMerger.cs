using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Fuses the per-context search batches with reciprocal rank fusion (spec §4.1): each
///     batch is one ranked list, contexts fuse at uniform weight, scores normalize to their
///     max (top result = 1.0), then minScore and limit apply.
/// </summary>
internal static class SearchResultMerger
{
    public static IReadOnlyList<MemorySearchResult> Merge(
        IEnumerable<IReadOnlyList<MemorySearchResult>> batches,
        int limit,
        double minScore = 0.0,
        int rrfK = SearchQuery.DefaultRrfK)
    {
        ArgumentNullException.ThrowIfNull(batches);

        var lists = batches
            .Select(batch => (batch, Weight: 1.0))
            .ToList();
        return ReciprocalRankFusion.Fuse(lists, rrfK, minScore, limit);
    }
}