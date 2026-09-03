using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>One calculator call: per-hash evidence in fusion order plus the response stats.</summary>
public sealed record FusionEvidenceResult(
    IReadOnlyList<RetrievalEvidence> Evidence,
    FusionStats? Stats);

/// <summary>
///     Pure Stage-1 evidence calculator over the §3 formulas. For a hash h, raw(h) is the sum of
///     weight/(k+rank) over the non-empty legs that returned it, and strength is raw(h) divided
///     by maxPossible (the same sum with rank 1 everywhere), so rank-1-in-every-firing-leg
///     always scores 1.0. No scorer, floor, weight, or default is read or changed here.
/// </summary>
public static class FusionEvidence
{
    /// <summary>
    ///     Builds per-hash evidence and response margins from raw leg orderings. Cosine is always
    ///     null here — the S2 capture lane fills it at its consumption point (where a NaN cosine
    ///     becomes null while strength and legs are kept).
    /// </summary>
    public static FusionEvidenceResult FromRaws(IReadOnlyList<NamedWeightedResults> legs, int k)
    {
        Guard.IsNotNull(legs);
        Guard.IsGreaterThan(k, 0);

        var participating = legs.Where(leg => leg.Results.Count > 0).ToList();
        if (participating.Count == 0)
        {
            return new FusionEvidenceResult([], null);
        }

        var maxPossible = 0.0;
        foreach (var leg in participating)
        {
            maxPossible += leg.Weight / (k + 1);
        }

        // All-weights-zero normalizes nothing: every strength would be 0/0.
        if (maxPossible == 0.0)
        {
            return new FusionEvidenceResult([], null);
        }

        var raws = new Dictionary<string, double>(StringComparer.Ordinal);
        var legRanks = new Dictionary<string, List<LegRank>>(StringComparer.Ordinal);
        foreach (var leg in participating)
        {
            for (var rank = 1; rank <= leg.Results.Count; rank++)
            {
                // Ranks are ordinal: only position matters, so a negative-magnitude producer
                // (BM25 is routinely negative) flows through like any other rank and nulls nothing.
                var hash = leg.Results[rank - 1];
                raws[hash] = raws.GetValueOrDefault(hash) + leg.Weight / (k + rank);
                if (!legRanks.TryGetValue(hash, out var ranks))
                {
                    ranks = [];
                    legRanks[hash] = ranks;
                }

                ranks.Add(new LegRank(leg.LegName, rank));
            }
        }

        var scored = new List<(string Hash, double Raw, RetrievalEvidence Evidence)>();
        foreach (var pair in raws)
        {
            // A non-finite weight-derived raw nulls that hash's evidence only; the search
            // continues for every other hash.
            if (!double.IsFinite(pair.Value))
            {
                continue;
            }

            var strength = pair.Value / maxPossible;
            if (!double.IsFinite(strength))
            {
                continue;
            }

            scored.Add((pair.Key, pair.Value, new RetrievalEvidence(pair.Key, strength, legRanks[pair.Key], null)));
        }

        scored.Sort(static (left, right) =>
        {
            var byRaw = right.Raw.CompareTo(left.Raw);
            return byRaw != 0 ? byRaw : string.CompareOrdinal(left.Hash, right.Hash);
        });

        var evidence = scored.Select(entry => entry.Evidence).ToList();
        if (scored.Count == 0 || scored[0].Raw <= 0.0)
        {
            return new FusionEvidenceResult(evidence, null);
        }

        double? topMargin = null;
        double? topVsMedian = null;
        if (scored.Count >= 2)
        {
            topMargin = (scored[0].Raw - scored[1].Raw) / scored[0].Raw;
        }

        if (scored.Count >= 3)
        {
            var middle = scored.Count / 2;
            var median = scored.Count % 2 == 1
                ? scored[middle].Raw
                : (scored[middle - 1].Raw + scored[middle].Raw) / 2.0;
            topVsMedian = (scored[0].Raw - median) / scored[0].Raw;
        }

        var stats = new FusionStats(
            topMargin,
            topVsMedian,
            maxPossible,
            [.. participating.Select(leg => leg.LegName)]);
        return new FusionEvidenceResult(evidence, stats);
    }
}
