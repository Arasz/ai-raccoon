namespace AiRaccoon.Core.Encryption;

/// <summary>Non-secret encryption source selection, mirrored in the sidecar and the settings table (plan §5.2).</summary>
/// <param name="Source">"env" or "bitwarden" (see <see cref="EncryptionSettingsKeys"/>).</param>
/// <param name="ProjectId">Bitwarden project id; null when the source is env.</param>
/// <param name="SecretId">Bitwarden secret id; null when the source is env.</param>
public sealed record EncryptionData(string Source)
{
    public const string NoneEncryptedSource = "none";

    public static readonly EncryptionData None = new(NoneEncryptedSource);

    public string? ProjectId { get; init; }
    public string? SecretId { get; init; }
}
