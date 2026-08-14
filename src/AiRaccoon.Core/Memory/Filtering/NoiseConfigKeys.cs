namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>Settings keys for pre-write noise rejection: the kill switch (mirrors AiRaccoon.Core.Degradation.SweepConfigKeys).</summary>
public static class NoiseConfigKeys
{
    public const string EnabledGlobal = "noise.enabled.global";

    public const bool DefaultEnabled = true;

    /// <summary>On unless the setting explicitly says "false": an absent or unreadable value keeps the default.</summary>
    public static bool ParseEnabled(string? value) => !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
