namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Resolves the passphrase used to encrypt the SQLite memory bank. Return null to disable encryption.
/// </summary>
public interface IEncryptionKeyProvider
{
    /// <summary>Returns the encryption passphrase, or null to open without encryption.</summary>
    string? GetPassphrase();
}
