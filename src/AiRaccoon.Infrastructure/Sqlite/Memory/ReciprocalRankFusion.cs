using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Reciprocal rank fusion (FR-NM-4; see docs/work/features-native-memory/native-memory.feature): score = sum of weight / (k + rank) per retrieving
///     list, normalized to max 1.0; the first list carrying a result supplies its payload.
/// </summary>
internal static class ReciprocalRankFusion
{
    // The leg whose candidate Ranking is the fused cosine (see BuildDualVectorResults):
    // the only Ranking this seam ever reads. Leg names reuse the ModalityLeg vocabulary.
    private const string VectorLegName = "vector";

    public static IReadOnlyList<MemorySearchResult> Fuse(
        IReadOnlyList<WeightedResults> results,
        int k,
        double minRelativeScore,
        int limit)
    {
        Guard.IsNotNull(results);
        // Unnamed inputs gain positional names for the evidence math only; the evidence is
        // discarded on this path, so naming can never perturb the served results.
        return FuseWithEvidence(
            [.. results.Select((input, index) => new NamedWeightedCandidates(input.Result, input.Weight, $"leg{index}"))],
            k, minRelativeScore, limit).Results;
    }

    /// <summary>
    ///     S1 capture: fuses exactly like <see cref="Fuse" /> while attaching each served hash's
    ///     pre-normalization evidence. Stats describe the pre-floor population; the sidecar
    ///     covers served rows only. A non-finite vector Ranking nulls that hash's Cosine and
    ///     keeps its strength and legs; every other leg's Ranking (negative BM25 is healthy)
    ///     is never read and flows through untouched.
    /// </summary>
    public static FuseWithEvidenceResult FuseWithEvidence(
        IReadOnlyList<NamedWeightedCandidates> legs,
        int k,
        double minRelativeScore,
        int limit)
    {
        Guard.IsNotNull(legs);
        Guard.IsGreaterThan(k, 0);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var payloads = new Dictionary<string, MemorySearchResult>(StringComparer.Ordinal);
        var cosines = new Dictionary<string, double>(StringComparer.Ordinal);
        var hashLegs = new List<NamedWeightedResults>();

        foreach (var leg in legs)
        {
            if (leg.Candidates.Count == 0)
            {
                continue;
            }

            var hashes = new List<string>(leg.Candidates.Count);
            var isVector = string.Equals(leg.LegName, VectorLegName, StringComparison.Ordinal);
            for (var rank = 1; rank <= leg.Candidates.Count; rank++)
            {
                var candidate = leg.Candidates[rank - 1];
                hashes.Add(candidate.Hash);
                scores[candidate.Hash] = scores.GetValueOrDefault(candidate.Hash) + leg.Weight / (k + rank);
                payloads.TryAdd(candidate.Hash, candidate);
                if (isVector)
                {
                    cosines.TryAdd(candidate.Hash, candidate.Ranking);
                }
            }

            hashLegs.Add(new NamedWeightedResults(hashes, leg.Weight, leg.LegName));
        }

        if (scores.Count == 0)
        {
            return new FuseWithEvidenceResult([], new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal), null);
        }

        var evidence = FusionEvidence.FromRaws(hashLegs, k);

        var max = scores.Values.Max();
        IReadOnlyList<MemorySearchResult> fused =
        [
            .. scores
                .Select(pair => payloads[pair.Key] with { Ranking = pair.Value / max })
                .Where(result => result.Ranking >= minRelativeScore)
                .OrderByDescending(result => result.Ranking)
                .ThenBy(result => result.Path, StringComparer.Ordinal)
                .Take(limit)
        ];

        // O(n) attach over the served rows only: floored-out hashes keep their shape in
        // Stats but never reach the S3 join, so they carry no sidecar entry.
        var served = new HashSet<string>(fused.Select(result => result.Hash), StringComparer.Ordinal);
        var evidenceByHash = new Dictionary<string, RetrievalEvidence>(served.Count, StringComparer.Ordinal);
        foreach (var item in evidence.Evidence)
        {
            if (!served.Contains(item.Hash))
            {
                continue;
            }

            evidenceByHash[item.Hash] = cosines.TryGetValue(item.Hash, out var cosine) && double.IsFinite(cosine)
                ? item with { Cosine = cosine }
                : item;
        }

        return new FuseWithEvidenceResult(fused, evidenceByHash, evidence.Stats);
    }
}

/// <summary>
///     One named fusion input carrying full candidates. Unlike P1's hash-only
///     <see cref="NamedWeightedResults" /> (the pure calculator's input), this keeps payloads
///     and the vector cosine so both survive to the consumption point.
/// </summary>
internal sealed record NamedWeightedCandidates(IReadOnlyList<MemorySearchResult> Candidates, double Weight, string LegName);

/// <summary>S1 capture result: served rows, their evidence by hash, and the response shape.</summary>
internal sealed record FuseWithEvidenceResult(
    IReadOnlyList<MemorySearchResult> Results,
    IReadOnlyDictionary<string, RetrievalEvidence> EvidenceByHash,
    FusionStats? Stats);

public sealed record WeightedResults(IReadOnlyList<MemorySearchResult> Result, double Weight);
