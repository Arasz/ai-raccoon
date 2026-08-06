namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the background shared-extraction service (CLI-only config channel).</summary>
public static class ExtractionConfigKeys
{
    public const string EnabledGlobal = "extract.enabled.global";

    public const string ModeGlobal = "extract.mode.global";

    public const string IntervalMinutesGlobal = "extract.interval-minutes.global";

    public const int DefaultIntervalMinutes = 60;

    public static bool ParseEnabled(string? value) => value == "true";

    public static ExtractMode ParseMode(string? value) =>
        value == "promote" ? ExtractMode.Promote : ExtractMode.Propose;

    public static int ParseIntervalMinutes(string? value) =>
        int.TryParse(value, out var minutes) && minutes > 0 ? minutes : DefaultIntervalMinutes;
}
