namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>A (hash, similarity) pair from one vector modality; Sim is cosine similarity in [-1, 1].</summary>
public readonly record struct VectorHit(string Hash, double Sim);

/// <summary>A fused dual-vector rank: Score = alpha * content sim + (1 - alpha) * structure sim.</summary>
public sealed record FusedRank(string Hash, double Score);

/// <summary>
///     Fixed-alpha fusion of content and structure similarities (see docs/adr/0004-dual-vector-structure-signal.md):
///     score = alpha * content + (1 - alpha) * structure, alpha default 0.5. A row with no structure
///     similarity is scored against zero, which is what makes the signal favour headed chunks at all
///     — measured, not incidental (docs/adr/0057).
/// </summary>
public static class StructureFusion
{
    public const double DefaultAlpha = 0.5;

    /// <summary>Settings key for the bank-scoped alpha; absent or unparsable falls back to <see cref="DefaultAlpha" />.</summary>
    public const string AlphaSettingKey = "retrieval.structureAlpha";

    /// <summary>vec0 cosine distance in [0, 2] to cosine similarity in [-1, 1] (embeddings are L2-normalized).</summary>
    public static double SimFromDistance(double distance) => 1.0 - distance;

    /// <summary>
    ///     An absent <paramref name="structureSim" /> scores as zero, so a row with no structure
    ///     embedding is capped at <paramref name="alpha" /> of what a headed row can reach. That
    ///     cap is deliberate and measured: scoring absent structure as content-only instead
    ///     regresses S3 3→4, S4 3→6, S6 3→10 and A2 1→2 (docs/adr/0057).
    /// </summary>
    public static double Fused(double contentSim, double? structureSim, double alpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(alpha);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 1.0);
        return alpha * contentSim + (1.0 - alpha) * (structureSim ?? 0.0);
    }

    /// <summary>
    ///     Ranks the union of both modalities' candidate hits by fused score, descending,
    ///     with an ordinal-hash tie-break so equal scores stay deterministic.
    /// </summary>
    public static IReadOnlyList<FusedRank> Rank(
        IEnumerable<VectorHit> content, IEnumerable<VectorHit> structure, double alpha, int limit)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var contentSims = content
            .GroupBy(hit => hit.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Sim, StringComparer.Ordinal);
        var structureSims = structure
            .GroupBy(hit => hit.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Sim, StringComparer.Ordinal);

        return
        [
            .. contentSims.Keys
                .Union(structureSims.Keys, StringComparer.Ordinal)
                .Select(hash =>
                {
                    var structureSim = structureSims.TryGetValue(hash, out var sim) ? sim : (double?)null;
                    return new FusedRank(hash, Fused(contentSims.GetValueOrDefault(hash), structureSim, alpha));
                })
                .OrderByDescending(rank => rank.Score)
                .ThenBy(rank => rank.Hash, StringComparer.Ordinal)
                .Take(limit)
        ];
    }
}
