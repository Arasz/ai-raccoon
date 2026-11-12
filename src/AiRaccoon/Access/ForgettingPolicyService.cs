using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Tools;

namespace AiRaccoon.Access;

/// <summary>
///     Forgetting knobs (FR-NM-2; see docs/work/features-native-memory/native-memory.feature): the sweep rating threshold (sweep.ttl_days was removed
///     by the single-channel ruling — only per-entry TTLs remain, as data not config). This MCP path enforces
///     Destructive; the CLI (SettingsCommands) deliberately doesn't, since it's the console that sets access modes.
/// </summary>
public sealed class ForgettingPolicyService(IMemoryStore store, IMemoryAccessGuard access) : IForgettingPolicyService
{
    private const string SweepThresholdSettingKey = SweepThreshold.SettingKey;

    public const double DefaultSweepThreshold = SweepThreshold.Default;

    /// <summary>
    ///     Reads the sweep threshold — a single bank-global setting, not scoped by project;
    ///     a read needs no caller to gate.
    /// </summary>
    public async Task<double> GetSweepThresholdAsync(CancellationToken cancellationToken = default)
    {
        var raw = await store.GetSettingAsync(SweepThresholdSettingKey, cancellationToken);
        return SweepThreshold.Parse(raw);
    }

    /// <summary>Sets or clears one entry's TTL, then reports it next to the rating gate a sweep also checks.</summary>
    public async Task<TtlResult> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default)
    {
        await access.EnsureAsync(projectId, AccessRequirement.Destructive, ToolNames.SetMemoryTtl,
            cancellationToken);
        EntryTtl.EnsureValid(ttlDays);

        // Wrong project and unknown hash are the same refusal here — one project must not learn another's
        // hashes. Only a row the caller's own project holds can be told apart as the wrong kind.
        if (!await store.SetEntryTtlAsync(projectId, hash, ttlDays, cancellationToken))
        {
            if (await store.GetMetadataAsync(projectId, hash, cancellationToken) is not null)
            {
                throw new NotAMemoryEntryException(hash, projectId);
            }

            throw new UnknownHashException(hash, projectId);
        }

        var threshold = await GetSweepThresholdAsync(cancellationToken);
        var metadata = await store.GetMetadataAsync(projectId, hash, cancellationToken);
        var rating = metadata?.Rating ?? RatingPolicy.DefaultBaseScore;
        return new TtlResult(hash, metadata?.TtlDays, rating, threshold,
            DegradationPolicy.CanEverExpire(rating, threshold, metadata?.TtlDays));
    }
}
