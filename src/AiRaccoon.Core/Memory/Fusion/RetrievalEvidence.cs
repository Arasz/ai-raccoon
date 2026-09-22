namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>One leg's ordinal vote for a hash: which leg agreed, and at which 1-based rank.</summary>
public sealed record LegRank(string LegName, int Rank);

/// <summary>
///     Absolute pre-normalization evidence for one hash: the fraction of the strongest agreement
///     this query could have produced, which legs agreed at which ranks, and the vector leg's raw
///     content-embedding cosine to the query when a vector leg participated and reported one
///     (never the alpha-fused content/structure score used for ordering).
/// </summary>
public sealed record RetrievalEvidence(
    string Hash,
    double FusionStrength,
    IReadOnlyList<LegRank> Legs,
    double? Cosine);

/// <summary>
///     One participating fusion input in rank order: hashes earliest-first, so a hash's rank is
///     its 1-based position. Empty inputs never reach the calculator (they hold no opinion).
/// </summary>
public sealed record NamedWeightedResults(
    IReadOnlyList<string> Results,
    double Weight,
    string LegName);
