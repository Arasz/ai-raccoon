using System.Globalization;

namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the background shared-extraction service (CLI-only config channel).</summary>
public static class ExtractionConfigKeys
{
    public const string EnabledGlobal = "extract.enabled.global";

    public const string ModeGlobal = "extract.mode.global";

    public const string IntervalMinutesGlobal = "extract.interval-minutes.global";

    public const string ExcludePrefixesGlobal = "extract.exclude.prefixes";

    public const string QueueCapacityGlobal = "extract.queue-capacity.global";

    public const string AutoPromoteThresholdGlobal = "extract.auto-promote-threshold.global";

    public const int DefaultIntervalMinutes = 30;

    public const int DefaultQueueCapacity = 1000;

    /// <summary>Bounds of the promotion score (see PromotionScorer): the threshold lives in the same range.</summary>
    public const double MinAutoPromoteThreshold = 0.0;

    public const double MaxAutoPromoteThreshold = 4.0;

    public static bool ParseEnabled(string? value) => value == "true";

    public static ExtractMode ParseMode(string? value) => value == "promote" ? ExtractMode.Promote : ExtractMode.Propose;

    public static int ParseIntervalMinutes(string? value) => int.TryParse(value, out var minutes) && minutes > 0 ? minutes : DefaultIntervalMinutes;

    /// <summary>Splits the comma-separated exclusion setting; absent/empty means no exclusions.</summary>
    public static IReadOnlyList<string> ParseExcludePrefixes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The propose tier's total cap; anything non-numeric or below 1 falls back to the default.</summary>
    public static int ParseQueueCapacity(string? value) => int.TryParse(value, out var capacity) && capacity > 0 ? capacity : DefaultQueueCapacity;

    /// <summary>
    ///     The score-gated auto-promote threshold, or null when auto-promotion is off. Unset,
    ///     blank, "off", unparseable and out-of-range values all mean off — an operator typo
    ///     must never silently widen cross-project sharing, and a corrupt row fails closed too.
    /// </summary>
    public static double? ParseAutoPromoteThreshold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            && threshold >= MinAutoPromoteThreshold
            && threshold <= MaxAutoPromoteThreshold)
        {
            return threshold;
        }

        return null;
    }

    /// <summary>Display form for the threshold setting: the invariant score, or "off" when disabled.</summary>
    public static string FormatAutoPromoteThreshold(double? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "off";
}
