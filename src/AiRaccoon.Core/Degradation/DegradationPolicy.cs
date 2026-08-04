namespace AiRaccoon.Core.Degradation;

/// <summary>
///     Candidate selection: an entry degrades only when it has an explicit per-entry TTL
///     and is both old enough and rated low enough (the global sweep.ttl_days knob was
///     removed by the single-channel ruling).
/// </summary>
public static class DegradationPolicy
{
    public static bool ShouldDegrade(double rating, double ageDays, double threshold, double? ttlDays) =>
        ttlDays.HasValue && rating < threshold && ageDays > ttlDays.Value;
}
