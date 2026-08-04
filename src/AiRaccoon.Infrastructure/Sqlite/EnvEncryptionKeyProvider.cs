namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Reads the encryption passphrase from the AIRACCOON_DB_PASSPHRASE environment variable.
///     Returns null (no encryption) when the variable is unset or empty.
/// </summary>
public sealed class EnvEncryptionKeyProvider : IEncryptionKeyProvider
{
    public const string EnvVarName = "AIRACCOON_DB_PASSPHRASE";

    public string? GetPassphrase()
    {
        var value = Environment.GetEnvironmentVariable(EnvVarName);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
