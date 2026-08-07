using System.Globalization;

namespace AiRaccoon.Core.Degradation;

/// <summary>The sweep rating threshold setting: key, default, and its invariant-culture parse/format.</summary>
public static class SweepThreshold
{
    public const string SettingKey = "sweep.threshold";

    public const double Default = 0.3;

    public static double Parse(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            ? threshold
            : Default;

    public static bool IsValid(double value) => value is >= 0.0 and <= 1.0;

    public static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
