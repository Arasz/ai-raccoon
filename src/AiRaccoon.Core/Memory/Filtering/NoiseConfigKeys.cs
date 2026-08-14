using System.Globalization;

namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>Settings keys for pre-write noise rejection: the kill switch (mirrors AiRaccoon.Core.Degradation.SweepConfigKeys).</summary>
public static class NoiseConfigKeys
{
    public const string EnabledGlobal = "noise.enabled.global";

    public const bool DefaultEnabled = true;

    /// <summary>On unless the setting explicitly says "false": an absent or unreadable value keeps the default.</summary>
    public static bool ParseEnabled(string? value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The self-learning noise subsystem's own kill switch (ADR-0039), independent of
    ///     <see cref="EnabledGlobal" />. Opposite default: a research lane has not yet verified the
    ///     approach works, so it must not reject writes until someone opts in explicitly.
    /// </summary>
    public const string LearnerEnabledGlobal = "noise.learner.enabled.global";

    public const bool DefaultLearnerEnabled = false;

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseLearnerEnabled(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public const string LearnerClusterDistanceThresholdGlobal = "noise.learner.cluster-distance-threshold.global";

    /// <summary>
    ///     The original (deleted) OnlineNoiseClusteringService's constructor default. No independent
    ///     derivation was ever recorded for it (ADR-0033/ADR-0039) — kept only as the settings
    ///     fallback, not re-hardcoded into a policy.
    /// </summary>
    public const double DefaultLearnerClusterDistanceThreshold = 0.12;

    /// <summary>Cosine distance is in [0, 2]; a threshold outside (0, 2] cannot mean anything, so it falls back to the default.</summary>
    public static double ParseLearnerClusterDistanceThreshold(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 2
            ? parsed
            : DefaultLearnerClusterDistanceThreshold;
}
