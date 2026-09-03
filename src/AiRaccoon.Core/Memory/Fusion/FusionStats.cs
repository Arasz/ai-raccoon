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
    IReadOnlyList<string> ParticipatingLegs)
{
    /// <summary>
    ///     The metric names the fusion-signal series record — declared once so emission
    ///     (MemoryTools) and tests never keep second hand-written copies
    ///     (derive-or-delete-the-list, FusionDiff.MetricNames precedent). Renaming an
    ///     exported series would orphan its stored metric rows: add, never rename.
    /// </summary>
    public const string TopStrengthMetric = "search.fusion.top_strength";
    public const string TopMarginMetric = "search.fusion.top_margin";
    public const string LegsFiredMetric = "search.fusion.legs_fired";

    public static IReadOnlyList<string> MetricNames { get; } = [TopStrengthMetric, TopMarginMetric, LegsFiredMetric];
}
