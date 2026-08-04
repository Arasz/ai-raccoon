using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Reciprocal rank fusion (FR-NM-4): a result's score is the weighted sum over the ranked
///     lists that retrieved it of weight / (k + rank); the fused scores are normalized to their
///     max so the top result is 1.0 (contract: ranking in 0..1). An empty list contributes
///     nothing — a result is scored by whichever modality retrieved it (COALESCE). The first
///     list carrying a result also supplies its payload, so the FTS list's snippet() wins.
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