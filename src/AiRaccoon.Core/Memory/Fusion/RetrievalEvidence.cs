namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>One leg's ordinal vote for a hash: which leg agreed, and at which 1-based rank.</summary>
public sealed record LegRank(string LegName, int Rank);

/// <summary>
///     Absolute pre-normalization evidence for one hash: the fraction of the strongest agreement
///     this query could have produced, which legs agreed at which ranks, and the fused vector
///     cosine when a vector leg participated (null until the S2 capture lane fills it in).
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
