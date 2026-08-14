namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>Settings keys for the read-path query guard: the kill switch and shadow mode (mirrors AiRaccoon.Core.Memory.Filtering.NoiseConfigKeys).</summary>
public static class QueryGuardConfigKeys
{
    public const string EnabledGlobal = "queryGuard.enabled.global";

    public const string ShadowGlobal = "queryGuard.shadow.global";

    // A false positive here costs one refused search, not lost data (contrast the write-path
    // noise filter, which discards content) — and the refuse tier is itself evidence-backed
    // (docs/adr/0040: 22/22 graded matches scored 2/5, never higher), so defaulting armed is safe.
    public const bool DefaultEnabled = true;

    public const bool DefaultShadow = false;

    /// <summary>On unless the setting explicitly says "false": an absent or unreadable value keeps the default.</summary>
    public static bool ParseEnabled(string? value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseShadow(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Structural detector (docs/adr/0041): a third input to the warn tier, gated separately from
    // the regex guard above because it is new and unproven where the regex tiers are
    // evidence-backed (docs/adr/0040). Default off on both paths, per the record's own
    // recommendation — this key is the read-path kill switch; the write path is not wired at all.
    public const string StructuralEnabledGlobal = "queryGuard.structural.enabled.global";

    public const bool DefaultStructuralEnabled = false;

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseStructuralEnabled(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The score threshold applied by <see cref="Structural.StructuralQueryGuardPolicy" />.
    ///     Default derives from out-of-fold calibration (5-fold CV) at the 2% target FPR on
    ///     exactly the 85% training split that produced the shipped model in
    ///     <c>StructuralNoiseModel.Trees.g.cs</c> (scripts/train-structural-noise-model.py,
    ///     docs/adr/0041 §Measurement) — not an invented constant, and every deployment should
    ///     recalibrate on its own query stream via <see cref="Structural.StructuralNoiseCalibrator" />
    ///     and set this key to override it (F8: the operating threshold does not transfer across
    ///     content distributions).
    /// </summary>
    public const string StructuralThresholdGlobal = "queryGuard.structural.threshold.global";

    public const double DefaultStructuralThreshold = 0.98939822280316;

    /// <summary>Falls back to <see cref="DefaultStructuralThreshold" /> when unset or unparsable.</summary>
    public static double ParseStructuralThreshold(string? value) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultStructuralThreshold;

    /// <summary>
    ///     Records which target FPR <see cref="DefaultStructuralThreshold" /> was calibrated for —
    ///     read by nothing at runtime; it exists so the threshold setting is never a number without
    ///     a stated target, and so an operator recalibrating with
    ///     <see cref="Structural.StructuralNoiseCalibrator" /> has a place to record their own
    ///     target alongside the resulting <see cref="StructuralThresholdGlobal" /> value.
    /// </summary>
    public const string StructuralTargetFprGlobal = "queryGuard.structural.targetFpr.global";

    public const double DefaultStructuralTargetFpr = 0.02;

    /// <summary>Falls back to <see cref="DefaultStructuralTargetFpr" /> when unset or unparsable.</summary>
    public static double ParseStructuralTargetFpr(string? value) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultStructuralTargetFpr;
}
