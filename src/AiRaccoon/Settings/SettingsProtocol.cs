namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane settings resource (ADR-0075), shared by the endpoint
///     that serves it and the store that calls it so the two halves cannot drift.
/// </summary>
internal static class SettingsProtocol
{
    public const string Path = "/settings";

    public static string ForKey(string key) => $"{Path}?key={Uri.EscapeDataString(key)}";

    public static string ForPrefix(string prefix) => $"{Path}?prefix={Uri.EscapeDataString(prefix)}";
}

/// <summary>A settings write. A blank key is refused by the endpoint.</summary>
internal sealed record SettingWrite(string Key, string Value);

/// <summary>A single key's value.</summary>
internal sealed record SettingValue(string Value);

/// <summary>Every row under a prefix; empty rather than absent when nothing matches.</summary>
internal sealed record SettingRows(IReadOnlyDictionary<string, string> Rows);
