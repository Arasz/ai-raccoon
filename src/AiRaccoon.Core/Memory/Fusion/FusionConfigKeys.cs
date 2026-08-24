namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>Settings key for the no-fusion-regression reorder (mirrors QueryGuardConfigKeys; docs/adr/0078).</summary>
public static class FusionConfigKeys
{
    // Default off, and there is no CliWriteOptOuts exception: the offline corpus cannot adjudicate
    // a fusion change (docs/adr/0072), so the enabled path is an evidence-gathering opt-in, not a
    // recommendation. Bank-wide on purpose — it is not a per-request tuning knob like rrfK.
    public const string NoRegressionEnabledGlobal = "fusion.noRegression.enabled.global";

    public const bool DefaultNoRegressionEnabled = false;

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseNoRegressionEnabled(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
