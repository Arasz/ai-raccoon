namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>A read-path query guard's tier: untouched, run-but-annotated, or never run. See docs/adr/0040.</summary>
public enum QueryGuardTier
{
    Clean,
    Warn,
    Refuse
}

/// <summary>The guard's verdict for one query: the tier, which policy matched, and the guidance to hand back.</summary>
public readonly record struct QueryGuardVerdict(QueryGuardTier Tier, string? PolicyName = null, string? Guidance = null)
{
    public static QueryGuardVerdict Clean { get; } = new(QueryGuardTier.Clean);

    public static QueryGuardVerdict Warn(string policyName, string guidance) => new(QueryGuardTier.Warn, policyName, guidance);

    public static QueryGuardVerdict Refuse(string policyName, string guidance) => new(QueryGuardTier.Refuse, policyName, guidance);
}
