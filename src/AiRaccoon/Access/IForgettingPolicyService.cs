using AiRaccoon.Core.Degradation;

namespace AiRaccoon.Access;

public interface IForgettingPolicyService
{
    /// <summary>
    ///     Reads the sweep threshold — a single bank-global setting, not scoped by project;
    ///     unlike <see cref="ForgettingPolicyService.SetSweepThresholdAsync" /> a read needs no caller to gate.
    /// </summary>
    Task<double> GetSweepThresholdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the sweep threshold — again a single bank-global setting: every project's
    ///     sweep reads the same value. <paramref name="projectId" /> is not a scope, only the caller's
    ///     proof of Destructive consent for changing a policy that affects every project's data.
    /// </summary>
    Task SetSweepThresholdAsync(string projectId, double threshold,
        CancellationToken cancellationToken = default);

    /// <summary>Sets or clears one entry's TTL, then reports it next to the rating gate a sweep also checks.</summary>
    Task<TtlResult> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default);
}
