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

    /// <summary>§3.3 D-E9: the code corpus's own activation write — POST-only, same reason as <see cref="ModelPath" />, but no outbox/migration record: the settings rows and the code_entries invalidation commit in one transaction and return.</summary>
    public const string ModelCodePath = "/settings/model/code";

    public static string ForKey(string key) => $"{Path}?key={Uri.EscapeDataString(key)}";

    public static string ForPrefix(string prefix) => $"{Path}?prefix={Uri.EscapeDataString(prefix)}";
}

/// <summary>A settings write. A blank key is refused by the endpoint.</summary>
internal sealed record SettingWrite(string Key, string Value);

/// <summary>A single key's value.</summary>
internal sealed record SettingValue(string Value);

/// <summary>Every row under a prefix; empty rather than absent when nothing matches.</summary>
internal sealed record SettingRows(IReadOnlyDictionary<string, string> Rows);

/// <summary>A `model embedding set` request (ADR-0076): provider is required, model/baseUrl are engine-specific.</summary>
internal sealed record ModelMigrationRequest(string Provider, string? Model, string? BaseUrl);

/// <summary>What the outbox transaction actually committed — the CLI's only feedback; the re-embed itself is reported nowhere (ruled: no progress channel).</summary>
internal sealed record ModelMigrationResponse(string Provider, string Model, string Engine);

/// <summary>A `model code set local` request (§3.3 D-E9): the directory is validated (manifest present) by the CLI before this is sent.</summary>
internal sealed record ModelCodeActivationRequest(string Directory);

/// <summary>What the activation transaction actually committed — the CLI's only feedback.</summary>
internal sealed record ModelCodeActivationResponse(string Model, string Engine);
