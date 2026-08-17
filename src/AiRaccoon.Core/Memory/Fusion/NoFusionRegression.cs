namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>
///     Enforces ADR-0006's declared "no fusion regression" rule as an ORDER over the fused list —
///     the only thing that survives the second fusion (ADR-0058). A result sorts by the better of
///     its fused position and its best contributing leg's position; absence from a leg is no claim,
///     and never a penalty. No tunable constant (docs/adr/0078).
/// </summary>
public static class NoFusionRegression
{
    public static IReadOnlyList<MemorySearchResult> Reorder(
        IReadOnlyList<MemorySearchResult> fused,
        IReadOnlyList<ModalityLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(fused);
        ArgumentNullException.ThrowIfNull(legs);

        var contributing = legs.Where(leg => leg.Contributes).ToList();
        if (contributing.Count < 2 || fused.Count == 0)
        {
            return fused;
        }

        var bestLegRank = BestLegRanks(contributing);
        return
        [
            .. fused
                .Select((result, index) => (Result: result, FusedRank: index + 1))
                .OrderBy(entry => Math.Min(entry.FusedRank,
                    bestLegRank.GetValueOrDefault(entry.Result.Hash, entry.FusedRank)))
                .ThenBy(entry => entry.FusedRank)
                .Select(entry => entry.Result)
        ];
    }

    private static Dictionary<string, int> BestLegRanks(IReadOnlyList<ModalityLeg> legs)
    {
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var leg in legs)
        {
            for (var rank = 1; rank <= leg.Candidates.Count; rank++)
            {
                var hash = leg.Candidates[rank - 1].Hash;
                if (!best.TryGetValue(hash, out var current) || rank < current)
                {
                    best[hash] = rank;
                }
            }
        }

        return best;
    }
}
