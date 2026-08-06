using AiRaccoon.Core.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

public sealed class NoneEncryptionKeyProvider : IEncryptionKeyProvider
{
    public const string EncryptionSource = EncryptionData.NoneEncryptedSource;
    public string Source => EncryptionSource;
    public bool IsForSource(string source) => string.IsNullOrWhiteSpace(source) || Source.Equals(source, StringComparison.Ordinal);

    public Passphrase GetPassphrase(EncryptionData encryptionData) => new(Source);
}
