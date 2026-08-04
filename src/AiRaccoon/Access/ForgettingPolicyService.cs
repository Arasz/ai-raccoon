using System.Globalization;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Access;

/// <summary>
///     Forgetting knobs (FR-NM-2): sweep threshold and per-entry ttl_days overrides. Both are
///     destructive adjustments, so they run behind the full-mode guard.
/// </summary>
public sealed class ForgettingPolicyService(IMemoryStore store, IMemoryAccessGuard access)
{
    public const string SweepThresholdSettingKey = "sweep.threshold";
    public const string SweepTtlDaysSettingKey = "sweep.ttl_days";

    public const double DefaultSweepThreshold = 0.3;

    public const double DefaultSweepTtlDays = 30;

    public async Task<double> GetSweepThresholdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var raw = await store.GetSettingAsync(SweepThresholdSettingKey, cancellationToken).ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            ? threshold
            : DefaultSweepThreshold;
    }

    public async Task<double> GetSweepTtlDaysAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var raw = await store.GetSettingAsync(SweepTtlDaysSettingKey, cancellationToken).ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ttl)
            ? ttl
            : DefaultSweepTtlDays;
    }

    public async Task SetSweepThresholdAsync(string projectId, double threshold,
        CancellationToken cancellationToken = default)
    {
        await access.EnsureAsync(projectId, AccessRequirement.Destructive, "memory_sweep_threshold",
                cancellationToken)
            .ConfigureAwait(false);
        await store.SetSettingAsync(SweepThresholdSettingKey,
                threshold.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default)
    {
        await access.EnsureAsync(projectId, AccessRequirement.Destructive, "memory_set_ttl",
                cancellationToken)
            .ConfigureAwait(false);
        await store.SetEntryTtlAsync(projectId, hash, ttlDays, cancellationToken).ConfigureAwait(false);
    }
}
