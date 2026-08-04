using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Reciprocal rank fusion (FR-NM-4): score = sum of weight / (k + rank) per retrieving
///     list, normalized to max 1.0; the first list carrying a result supplies its payload.
/// </summary>
internal static class ReciprocalRankFusion
{
    public static IReadOnlyList<MemorySearchResult> Fuse(
        IReadOnlyList<(IReadOnlyList<MemorySearchResult> List, double Weight)> lists,
        int k,
        double minScore,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(lists);
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var payloads = new Dictionary<string, MemorySearchResult>(StringComparer.Ordinal);

        foreach (var (list, weight) in lists)
        {
            if (list.Count == 0)
            {
                continue;
            }

            for (var rank = 1; rank <= list.Count; rank++)
            {
                var result = list[rank - 1];
                scores[result.Hash] = scores.GetValueOrDefault(result.Hash) + weight / (k + rank);
                payloads.TryAdd(result.Hash, result);
            }
        }

        if (scores.Count == 0)
        {
            return [];
        }

        var max = scores.Values.Max();
        return
        [
            .. scores
                .Select(pair => payloads[pair.Key] with { Ranking = pair.Value / max })
                .Where(result => result.Ranking >= minScore)
                .OrderByDescending(result => result.Ranking)
                .ThenBy(result => result.Path, StringComparer.Ordinal)
                .Take(limit)
        ];
    }
}