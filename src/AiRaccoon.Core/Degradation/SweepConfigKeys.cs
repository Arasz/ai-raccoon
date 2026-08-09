namespace AiRaccoon.Core.Degradation;

/// <summary>Settings keys for the background sweep (reaper) service: the kill switch and the cadence.</summary>
public static class SweepConfigKeys
{
    public const string EnabledGlobal = "sweep.enabled.global";

    public const string IntervalHoursGlobal = "sweep.interval-hours.global";

    public const bool DefaultEnabled = true;

    public const int DefaultIntervalHours = 24;

    public const int MinIntervalHours = 1;

    /// <summary>A year: beyond it the cadence is indistinguishable from disabling the sweep.</summary>
    public const int MaxIntervalHours = 8760;

    /// <summary>On unless the setting explicitly says "false": an absent or unreadable value keeps the default.</summary>
    public static bool ParseEnabled(string? value) =>
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public static int ParseIntervalHours(string? value) =>
        int.TryParse(value, out var hours)
            ? Math.Clamp(hours, MinIntervalHours, MaxIntervalHours)
            : DefaultIntervalHours;
}
