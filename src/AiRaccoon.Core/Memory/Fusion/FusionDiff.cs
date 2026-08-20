namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>
///     How the no-fusion-regression reorder changed the served list against the baseline one,
///     collected only on the flag-enabled path (docs/adr/0078). Joined to `search_quality` by
///     correlation id, which is what turns movement into a verdict.
/// </summary>
public sealed record FusionDiff(double Top1Changed, double Top1RankDelta, double Top5Moved)
{
    /// <summary>The baseline top result is not in the served list at all — not a delta of zero.</summary>
    public const double Dropped = -1;

    private const string Top1ChangedMetric = "search.fusion.top1_changed";

    private const string Top1RankDeltaMetric = "search.fusion.top1_rank_delta";

    private const string Top5MovedMetric = "search.fusion.top5_moved";

    private const int Window = 5;

    /// <summary>Declared, not reflected, for the same reason as SearchTimings.PhaseNames.</summary>
    public static IReadOnlyList<string> MetricNames { get; } = [Top1ChangedMetric, Top1RankDeltaMetric, Top5MovedMetric];

    /// <summary>This instance as measurement rows, in the same order as <see cref="MetricNames" />.</summary>
    public IReadOnlyList<(string Name, double Value, string Unit)> Measurements() =>
    [
        (MetricNames[0], Top1Changed, "flag"),
        (MetricNames[1], Top1RankDelta, "ranks"),
        (MetricNames[2], Top5Moved, "results")
    ];

    public static FusionDiff Between(
        IReadOnlyList<MemorySearchResult> baseline,
        IReadOnlyList<MemorySearchResult> adjusted)
    {
        if (baseline.Count == 0 || adjusted.Count == 0)
        {
            return new FusionDiff(0, 0, 0);
        }

        var winner = baseline[0].Hash;
        var moved = IndexOf(adjusted, winner);
        var window = Math.Min(Window, Math.Min(baseline.Count, adjusted.Count));
        var top5Moved = Enumerable.Range(0, window)
            .Count(position => !string.Equals(baseline[position].Hash, adjusted[position].Hash, StringComparison.Ordinal));

        return new FusionDiff(
            string.Equals(winner, adjusted[0].Hash, StringComparison.Ordinal) ? 0 : 1,
            moved < 0 ? Dropped : moved,
            top5Moved);
    }

    private static int IndexOf(IReadOnlyList<MemorySearchResult> results, string hash)
    {
        for (var index = 0; index < results.Count; index++)
        {
            if (string.Equals(results[index].Hash, hash, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
