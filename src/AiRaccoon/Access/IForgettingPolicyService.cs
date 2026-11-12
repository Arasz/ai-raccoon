using AiRaccoon.Core.Degradation;

namespace AiRaccoon.Access;

public interface IForgettingPolicyService
{
    /// <summary>
    ///     Reads the sweep threshold — a single bank-global setting, not scoped by project;
    ///     a read needs no caller to gate.
    /// </summary>
    Task<double> GetSweepThresholdAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets or clears one entry's TTL, then reports it next to the rating gate a sweep also checks.</summary>
    Task<TtlResult> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default);
}
