namespace AiRaccoon.Core.Degradation;

/// <summary>Candidate selection: an entry degrades only when it is both old enough and rated low enough.</summary>
public static class DegradationPolicy
{
    public static bool ShouldDegrade(double rating, double ageDays, double threshold, double ttlDays) => rating < threshold && ageDays > ttlDays;

    public static bool ShouldDegrade(double rating, double ageDays, double threshold, double ttlDays, double? ttlOverrideDays) => rating < threshold && ageDays > (ttlOverrideDays ?? ttlDays);
}
