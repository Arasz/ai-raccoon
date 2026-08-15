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
///         Disabled reads one setting and returns Clean untouched — byte-identical to no guard at
///         all. The structural detector only ever runs when the regex tiers found nothing: it is a
///         third input to the warn tier, never able to override a regex Refuse or Warn, and gated
///         by its own default-off setting. Shadow mode logs the verdict and returns Clean.
///     </para>
/// </summary>
public sealed class QueryGuardService(ISettingsStore settings) : IQueryGuardService
{
    public async Task<QueryGuardOutcome> EvaluateAsync(string projectId, string query,
        CancellationToken cancellationToken = default)
    {
        var enabled = QueryGuardConfigKeys.ParseEnabled(
            await settings.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, cancellationToken).ConfigureAwait(false));
        if (!enabled)
        {
            return new QueryGuardOutcome(QueryGuardVerdict.Clean);
        }

        var verdict = QueryGuardPolicy.Evaluate(query);
        if (verdict.Tier == QueryGuardTier.Clean)
        {
            verdict = await EvaluateStructuralAsync(query, cancellationToken).ConfigureAwait(false) ?? verdict;
        }

        if (verdict.Tier == QueryGuardTier.Clean)
        {
            return new QueryGuardOutcome(verdict);
        }

        var shadow = QueryGuardConfigKeys.ParseShadow(
            await settings.GetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, cancellationToken).ConfigureAwait(false));
        return shadow
            ? new QueryGuardOutcome(QueryGuardVerdict.Clean, verdict)
            : new QueryGuardOutcome(verdict);
    }

    /// <summary>
    ///     Default off (<see cref="QueryGuardConfigKeys.DefaultStructuralEnabled" />), so the settings
    ///     read below only happens when an operator has explicitly opted in. Returns null — defer to
    ///     the regex verdict — unless the score clears the calibrated threshold; never returns Refuse.
    /// </summary>
    private async Task<QueryGuardVerdict?> EvaluateStructuralAsync(string query, CancellationToken cancellationToken)
    {
        var structuralEnabled = QueryGuardConfigKeys.ParseStructuralEnabled(
            await settings.GetSettingAsync(QueryGuardConfigKeys.StructuralEnabledGlobal, cancellationToken)
                .ConfigureAwait(false));
        if (!structuralEnabled)
        {
            return null;
        }

        var threshold = QueryGuardConfigKeys.ParseStructuralThreshold(
            await settings.GetSettingAsync(QueryGuardConfigKeys.StructuralThresholdGlobal, cancellationToken)
                .ConfigureAwait(false));

        var verdict = StructuralQueryGuardPolicy.Evaluate(query, threshold);
        return verdict.Tier == QueryGuardTier.Warn ? verdict : null;
    }
}
