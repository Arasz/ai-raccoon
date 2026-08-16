namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane settings resource (ADR-0075), shared by the endpoint
///     that serves it and the store that calls it so the two halves cannot drift.
/// </summary>
internal static class SettingsProtocol
{
    public const string Path = "/settings";

    /// <summary>ADR-0076: the model-migration outbox write. Not a settings key/value route — POST-only, no GET twin, since the CLI never reads a migration record directly.</summary>
    public const string ModelPath = "/settings/model";

    public static string ForKey(string key) => $"{Path}?key={Uri.EscapeDataString(key)}";

    public static string ForPrefix(string prefix) => $"{Path}?prefix={Uri.EscapeDataString(prefix)}";
}

/// <summary>A settings write. A blank key is refused by the endpoint.</summary>
internal sealed record SettingWrite(string Key, string Value);

/// <summary>A single key's value.</summary>
internal sealed record SettingValue(string Value);

/// <summary>Every row under a prefix; empty rather than absent when nothing matches.</summary>
internal sealed record SettingRows(IReadOnlyDictionary<string, string> Rows);

/// <summary>A `model set` request (ADR-0076): provider is required, model/baseUrl are engine-specific.</summary>
internal sealed record ModelMigrationRequest(string Provider, string? Model, string? BaseUrl);

/// <summary>What the outbox transaction actually committed — the CLI's only feedback; the re-embed itself is reported nowhere (ruled: no progress channel).</summary>
internal sealed record ModelMigrationResponse(string Provider, string Model, string Engine);
