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
}
