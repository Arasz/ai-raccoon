namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>Settings keys for pre-write noise rejection: the kill switch (mirrors AiRaccoon.Core.Degradation.SweepConfigKeys).</summary>
public static class NoiseConfigKeys
{
    public const string EnabledGlobal = "noise.enabled.global";

    public const bool DefaultEnabled = true;

    /// <summary>On unless the setting explicitly says "false": an absent or unreadable value keeps the default.</summary>
    public static bool ParseEnabled(string? value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reserved for a future enforce mode (real write rejection from a learned cluster) —
    ///     nothing reads this setting yet. <see cref="LearnerShadowEnabledGlobal" /> is what the
    ///     write path actually consults today; enforcement is deliberately not built until shadow
    ///     mode has produced the per-install evidence to justify it (ADR-0039).
    /// </summary>
    public const string LearnerEnabledGlobal = "noise.learner.enabled.global";

    public const bool DefaultLearnerEnabled = false;

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseLearnerEnabled(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Shadow/dry-run mode (ADR-0039, revised direction): evaluates the learner against real
    ///     traffic and records what it would have rejected — without ever rejecting anything — so an
    ///     operator can measure their own true/false-positive rate before <see cref="LearnerEnabledGlobal" />
    ///     (enforcement) is ever considered. This is the setting the write path actually reads today;
    ///     <see cref="LearnerEnabledGlobal" /> stays reserved and unread pending that decision.
    /// </summary>
    public const string LearnerShadowEnabledGlobal = "noise.learner.shadow.enabled.global";

    public const bool DefaultLearnerShadowEnabled = false;

    /// <summary>Off unless the setting explicitly says "true": an absent or unreadable value keeps the default.</summary>
    public static bool ParseLearnerShadowEnabled(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // No similarity-threshold setting here (ADR-0039): a distance cutoff is itself scoring logic,
    // and no detector — centroid or otherwise — has been validated on held-out data. That threshold
    // existed in an earlier draft of this file and was removed once the write path stopped doing
    // any distance comparison; see docs/adr/0039 for why.

    /// <summary>How long a rejected write stays in noise_entries before the maintenance purge removes it.</summary>
    public const string RetentionDaysGlobal = "noise.retention-days.global";

    /// <summary>ADR-0029's original hardcoded value, now a setting default rather than a constant.</summary>
    public const int DefaultRetentionDays = 14;

    public static int ParseRetentionDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultRetentionDays;
}
