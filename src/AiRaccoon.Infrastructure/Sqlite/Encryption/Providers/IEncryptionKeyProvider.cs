using AiRaccoon.Core.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

/// <summary>
///     Resolves the passphrase used to encrypt the SQLite memory bank. Return null to disable encryption.
/// </summary>
public interface IEncryptionKeyProvider
{
    public string Source { get; }

    bool IsForSource(string source);

    /// <summary>Returns the encryption passphrase, or null to open without encryption.</summary>
    Passphrase GetPassphrase(EncryptionData encryptionData);
}

public sealed record Passphrase(string Source)
{
    public string? Value { get; init; }

    /// <summary>
    ///     The same key under the pre-ADR-0012 derivation, for sources that can produce one — the
    ///     only thing that authorises a rekey. Null when the source has no legacy form (env, none).
    /// </summary>
    public string? LegacyValue { get; init; }
}
