using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>
///     Selects the encryption key provider from the memory.db.source sidecar, read fresh on every
///     call (the config commands change it between calls). Absent sidecar or "env" → the env
///     provider; "bitwarden" → bws fetch + derivation; a corrupt sidecar fails loudly (plan §5.2).
/// </summary>
public sealed class EncryptionKeyResolver(IEncryptionState encryptionState, IReadOnlyCollection<IEncryptionKeyProvider> providers)
    : IEncryptionKeyResolver
{
    public ResolvedKey Resolve()
    {
        var encryptionData = encryptionState.Read();
        var source = encryptionData.Source == EncryptionData.NoneEncryptedSource ? "env" : encryptionData.Source;
        var resolvedPassphrase = providers.Where(provider => provider.IsForSource(source))
            .Select(encryptionKeyProvider => encryptionKeyProvider.GetPassphrase(encryptionData))
            .FirstOrDefault() ?? ThrowHelper.ThrowArgumentNullException<Passphrase>();

        return new ResolvedKey(resolvedPassphrase.Value, resolvedPassphrase.Source);
    }
}

/// <summary>One resolution: the passphrase (null = unencrypted) and the source it came from.</summary>
public sealed record ResolvedKey(string? Passphrase, string SourceName)
{
    public static readonly ResolvedKey None = new(null, "");
}
