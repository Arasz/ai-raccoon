using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;

namespace AiRaccoon.Access;

/// <summary>
///     Forgetting knobs (FR-NM-2; see docs/work/features-native-memory/native-memory.feature): the sweep rating threshold (sweep.ttl_days was removed
///     by the single-channel ruling — only per-entry TTLs remain, as data not config). This MCP path enforces
///     Destructive; the CLI (SettingsCommands) deliberately doesn't, since it's the console that sets access modes.
/// </summary>
public sealed class ForgettingPolicyService(IMemoryStore store, IMemoryAccessGuard access)
{
    public const string SweepThresholdSettingKey = SweepThreshold.SettingKey;

    public const double DefaultSweepThreshold = SweepThreshold.Default;

    public async Task<double> GetSweepThresholdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var raw = await store.GetSettingAsync(SweepThresholdSettingKey, cancellationToken).ConfigureAwait(false);
        return SweepThreshold.Parse(raw);
    }


    public async Task SetSweepThresholdAsync(string projectId, double threshold,
        CancellationToken cancellationToken = default)
    {
        await access.EnsureAsync(projectId, AccessRequirement.Destructive, "memory_sweep_threshold",
                cancellationToken)
            .ConfigureAwait(false);
        await store.SetSettingAsync(SweepThresholdSettingKey,
                SweepThreshold.Format(threshold), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Sets or clears one entry's TTL, then reports it next to the rating gate a sweep also checks.</summary>
    public async Task<TtlResult> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default)
    {
        await access.EnsureAsync(projectId, AccessRequirement.Destructive, "memory_set_ttl",
                cancellationToken)
            .ConfigureAwait(false);
        EntryTtl.EnsureValid(ttlDays);

        // Wrong project and unknown hash are the same refusal here — one project must not learn another's hashes.
        if (!await store.SetEntryTtlAsync(projectId, hash, ttlDays, cancellationToken).ConfigureAwait(false))
        {
            throw new UnknownHashException(hash, projectId);
        }

        var threshold = await GetSweepThresholdAsync(projectId, cancellationToken).ConfigureAwait(false);
        var metadata = await store.GetMetadataAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
        var rating = metadata?.Rating ?? RatingPolicy.DefaultBaseScore;
        return new TtlResult(hash, metadata?.TtlDays, rating, threshold,
            DegradationPolicy.CanEverExpire(rating, threshold, metadata?.TtlDays));
    }
}
