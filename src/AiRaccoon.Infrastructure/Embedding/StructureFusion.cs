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
    /// <summary>vec0 cosine distance in [0, 2] to cosine similarity in [-1, 1] (embeddings are L2-normalized).</summary>
    public static double SimFromDistance(double distance) => 1.0 - distance;

    private static double Rescale(double sim, double floor) => floor > 0 ? Math.Max(0.0, (sim - floor) / (1.0 - floor)) : sim;

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
    ///     Ranks both modalities' hits by fused score (ADR-0004), each similarity first rescaled so the
    ///     engine's relevance floor maps to 0 (ADR-0108). Ties, such as rows the floor clamps to 0,
    ///     order by raw content similarity, then ordinal hash.
    /// </summary>
    public static IReadOnlyList<FusedRank> Rank(
        IEnumerable<VectorHit> content, IEnumerable<VectorHit> structure, double alpha, int limit, double similarityFloor = 0.0)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var rawContentSims = content
            .GroupBy(hit => hit.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Sim, StringComparer.Ordinal);
        var structureSims = structure
            .GroupBy(hit => hit.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Rescale(group.First().Sim, similarityFloor), StringComparer.Ordinal);

        return
        [
            .. rawContentSims.Keys
                .Union(structureSims.Keys, StringComparer.Ordinal)
                .Select(hash =>
                {
                    var hasContent = rawContentSims.TryGetValue(hash, out var rawContent);
                    var structureSim = structureSims.TryGetValue(hash, out var sim) ? sim : (double?)null;
                    var contentSim = hasContent ? Rescale(rawContent, similarityFloor) : 0.0;
                    return (Rank: new FusedRank(hash, Fused(contentSim, structureSim, alpha)),
                        RawContent: hasContent ? rawContent : double.NegativeInfinity);
                })
                .OrderByDescending(ranked => ranked.Rank.Score)
                .ThenByDescending(ranked => ranked.RawContent)
                .ThenBy(ranked => ranked.Rank.Hash, StringComparer.Ordinal)
                .Take(limit)
                .Select(ranked => ranked.Rank)
        ];
    }
}
