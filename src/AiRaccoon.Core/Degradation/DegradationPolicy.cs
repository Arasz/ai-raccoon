namespace AiRaccoon.Core.Degradation;

/// <summary>
///     Candidate selection: an entry degrades only when it has an explicit per-entry TTL
///     and is both old enough and rated low enough (the global sweep.ttl_days knob was
///     removed by the single-channel ruling).
/// </summary>
public static class DegradationPolicy
{
    public static bool ShouldDegrade(double rating, double ageDays, double threshold, int? ttlDays) => ttlDays.HasValue && rating < threshold && ageDays > ttlDays.Value;

    /// <summary>
    ///     Whether age alone now decides: a TTL is set and the rating gate is already met.
    ///     False means no age ever expires the entry until its rating falls below the threshold.
    /// </summary>
    public static bool CanEverExpire(double rating, double threshold, int? ttlDays) => ttlDays.HasValue && rating < threshold;
}
