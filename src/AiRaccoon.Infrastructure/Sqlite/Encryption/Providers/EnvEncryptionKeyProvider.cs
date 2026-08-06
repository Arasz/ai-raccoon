using AiRaccoon.Core.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

/// <summary>
///     Reads the encryption passphrase from the AIRACCOON_DB_PASSPHRASE environment variable.
///     Returns null (no encryption) when the variable is unset or empty.
/// </summary>
public sealed class EnvEncryptionKeyProvider : IEncryptionKeyProvider
{
    public const string EnvVarName = "AIRACCOON_DB_PASSPHRASE";


    public const string EncryptionSource = "env";

    public string Source => EncryptionSource;

    public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

    public Passphrase GetPassphrase(EncryptionData encryptionData)
    {
        var value = Environment.GetEnvironmentVariable(EnvVarName);
        return new Passphrase(Source)
        {
            Value = string.IsNullOrEmpty(value) ? null : value
        };
    }
}
