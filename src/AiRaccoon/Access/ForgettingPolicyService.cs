using System.Globalization;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Access;

/// <summary>
///     Forgetting knobs (FR-NM-2; see docs/work/features-native-memory/native-memory.feature): the sweep rating threshold (sweep.ttl_days was removed
///     by the single-channel ruling — only per-entry TTLs remain, as data not config).
/// </summary>
public sealed class ForgettingPolicyService(IMemoryStore store, IMemoryAccessGuard access)
{
    public const string SweepThresholdSettingKey = "sweep.threshold";

    public const double DefaultSweepThreshold = 0.3;

    public async Task<double> GetSweepThresholdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var raw = await store.GetSettingAsync(SweepThresholdSettingKey, cancellationToken).ConfigureAwait(false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            ? threshold
            : DefaultSweepThreshold;
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
