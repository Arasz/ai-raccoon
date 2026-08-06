namespace AiRaccoon.Core.Encryption;

/// <summary>
///     Settings-table keys and source values for the encryption provider family (plan §5.2).
///     Contract with S2b/S3/S5 — changing these breaks' integration.
/// </summary>
public static class EncryptionSettingsKeys
{
    public const string Source = "encryption.source";
    public const string ProjectId = "encryption.bitwarden.projectId";
    public const string SecretId = "encryption.bitwarden.secretId";
}
