namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the background shared-extraction service (CLI-only config channel).</summary>
public static class ExtractionConfigKeys
{
    public const string EnabledGlobal = "extract.enabled.global";

    public const string ModeGlobal = "extract.mode.global";

    public const string IntervalMinutesGlobal = "extract.interval-minutes.global";

    public const string ExcludePrefixesGlobal = "extract.exclude.prefixes";

    public const string QueueCapacityGlobal = "extract.queue-capacity.global";

    public const int DefaultIntervalMinutes = 30;

    public const int DefaultQueueCapacity = 1000;

    public static bool ParseEnabled(string? value) => value == "true";

    public static ExtractMode ParseMode(string? value) =>
        value == "promote" ? ExtractMode.Promote : ExtractMode.Propose;

    public static int ParseIntervalMinutes(string? value) =>
        int.TryParse(value, out var minutes) && minutes > 0 ? minutes : DefaultIntervalMinutes;

    /// <summary>Splits the comma-separated exclusion setting; absent/empty means no exclusions.</summary>
    public static IReadOnlyList<string> ParseExcludePrefixes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The propose tier's total cap; anything non-numeric or below 1 falls back to the default.</summary>
    public static int ParseQueueCapacity(string? value) =>
        int.TryParse(value, out var capacity) && capacity > 0 ? capacity : DefaultQueueCapacity;
}
