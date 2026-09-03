namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>
///     Response-level shape of the pre-normalization fusion distribution: the margin structure
///     that exposes thin responses. Computed on pre-max raws; a margin stays null until enough
///     results exist to define it.
/// </summary>
public sealed record FusionStats(
    double? TopMargin,
    double? TopVsMedian,
    double MaxPossible,
    IReadOnlyList<string> ParticipatingLegs);
