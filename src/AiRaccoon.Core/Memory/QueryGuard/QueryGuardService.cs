using AiRaccoon.Core.Memory.QueryGuard.Structural;

namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>
///     What the guard decided, and what shadow mode suppressed. `Shadowed` is non-null only when
///     shadow mode turned a real verdict into Clean — Core does not log (it holds no logging
///     dependency, and that is the layering rule), so the caller reports it.
/// </summary>
public sealed record QueryGuardOutcome(QueryGuardVerdict Verdict, QueryGuardVerdict? Shadowed = null);

/// <summary>The read-path query guard, reachable without going through MCP (docs/adr/0065).</summary>
public interface IQueryGuardService
{
    Task<QueryGuardOutcome> EvaluateAsync(string projectId, string query, CancellationToken cancellationToken = default);
}

/// <summary>
///     The tiered read-path guard (docs/adr/0040, docs/adr/0041), lifted out of `MemoryTools`
///     where it read its own settings inside the tools file and no other caller could reach it.
///     <para>
///         Disabled makes one settings-store call and returns Clean untouched — byte-identical to no
///         guard at all. The structural detector only ever runs when the regex tiers found nothing: it is a
///         third input to the warn tier, never able to override a regex Refuse or Warn, and gated
///         by its own default-off setting. Shadow mode logs the verdict and returns Clean.
///     </para>
/// </summary>
public sealed class QueryGuardService(ISettingsStore settings) : IQueryGuardService
{
    /// <summary>Covers every queryGuard.* key this service reads (docs/plans/2026-08-16-bank-open-cost-implementation.md WP2).</summary>
    private const string SettingsPrefix = "queryGuard.";

    public async Task<QueryGuardOutcome> EvaluateAsync(string projectId, string query,
        CancellationToken cancellationToken = default)
    {
        var values = await settings.GetSettingsByPrefixAsync(SettingsPrefix, cancellationToken).ConfigureAwait(false);

        var enabled = QueryGuardConfigKeys.ParseEnabled(values.GetValueOrDefault(QueryGuardConfigKeys.EnabledGlobal));
        if (!enabled)
        {
            return new QueryGuardOutcome(QueryGuardVerdict.Clean);
        }

        var verdict = QueryGuardPolicy.Evaluate(query);
        if (verdict.Tier == QueryGuardTier.Clean)
        {
            verdict = EvaluateStructural(query, values) ?? verdict;
        }

        if (verdict.Tier == QueryGuardTier.Clean)
        {
            return new QueryGuardOutcome(verdict);
        }

        var shadow = QueryGuardConfigKeys.ParseShadow(values.GetValueOrDefault(QueryGuardConfigKeys.ShadowGlobal));
        return shadow
            ? new QueryGuardOutcome(QueryGuardVerdict.Clean, verdict)
            : new QueryGuardOutcome(verdict);
    }

    /// <summary>
    ///     Default off (<see cref="QueryGuardConfigKeys.DefaultStructuralEnabled" />). Returns null —
    ///     defer to the regex verdict — unless the score clears the calibrated threshold; never
    ///     returns Refuse.
    /// </summary>
    private static QueryGuardVerdict? EvaluateStructural(string query, IReadOnlyDictionary<string, string> values)
    {
        var structuralEnabled = QueryGuardConfigKeys.ParseStructuralEnabled(
            values.GetValueOrDefault(QueryGuardConfigKeys.StructuralEnabledGlobal));
        if (!structuralEnabled)
        {
            return null;
        }

        var threshold = QueryGuardConfigKeys.ParseStructuralThreshold(
            values.GetValueOrDefault(QueryGuardConfigKeys.StructuralThresholdGlobal));

        var verdict = StructuralQueryGuardPolicy.Evaluate(query, threshold);
        return verdict.Tier == QueryGuardTier.Warn ? verdict : null;
    }
}
