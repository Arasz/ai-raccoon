namespace AiRaccoon.Core.Encryption;

/// <summary>
///     Settings-table keys and source values for the encryption provider family
///     (docs/plans/encryption-bitwarden-implementation.md §5.2). Changing them breaks integration.
/// </summary>
public static class EncryptionSettingsKeys
{
    public const string Source = "encryption.source";
    public const string ProjectId = "encryption.bitwarden.projectId";
    public const string SecretId = "encryption.bitwarden.secretId";
}
