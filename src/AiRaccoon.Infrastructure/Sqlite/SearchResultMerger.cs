using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Merges per-context search batches by hash keeping the best ranking, then sorts by ranking desc and limits (spec §4.1).</summary>
internal static class SearchResultMerger
{
    public static IReadOnlyList<MemorySearchResult> Merge(
        IEnumerable<IReadOnlyList<MemorySearchResult>> batches, int limit)
    {
        var bestByHash = new Dictionary<string, MemorySearchResult>(StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            foreach (var result in batch)
            {
                if (!bestByHash.TryGetValue(result.Hash, out var existing) || result.Ranking > existing.Ranking)
                {
                    bestByHash[result.Hash] = result;
                }
            }
        }

        return
        [
            .. bestByHash.Values
                .OrderByDescending(r => r.Ranking)
                .Take(limit)
        ];
    }
}
