using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>
///     Selects the encryption key provider from the memory.db.source sidecar, read fresh on every
///     call (the config commands change it between calls). Absent sidecar or "env" → the env
///     provider; "bitwarden" → bws fetch + derivation; a corrupt sidecar fails loudly (plan §5.2).
/// </summary>
public sealed class EncryptionKeyResolver(IEncryptionSourceSidecar encryptionState, IReadOnlyCollection<IEncryptionKeyProvider> providers)
    : IEncryptionKeyResolver
{
    public ResolvedKey Resolve()
    {
        var encryptionData = encryptionState.Read();
        var source = encryptionData.Source == EncryptionData.NoneEncryptedSource ? EnvEncryptionKeyProvider.EncryptionSource : encryptionData.Source;
        var resolvedPassphrase = providers.Where(provider => provider.IsForSource(source))
            .Select(encryptionKeyProvider => encryptionKeyProvider.GetPassphrase(encryptionData))
            .FirstOrDefault() ?? ThrowHelper.ThrowArgumentNullException<Passphrase>();

        return new ResolvedKey(resolvedPassphrase.Value, resolvedPassphrase.Source);
    }

    /// <summary>Creates a resolver with the standard three-provider chain for one-shot paths (config verbs).</summary>
    public static EncryptionKeyResolver Create(string bankPath, ICliSecretManager? bws = null)
    {
        var sidecar = new EncryptionSourceSidecar(bankPath);
        bws ??= new BitwardenCliSecretManager();
        return new EncryptionKeyResolver(sidecar,
            [new NoneEncryptionKeyProvider(), new EnvEncryptionKeyProvider(), new BitwardenEncryptionKeyProvider(bws)]);
    }
}

/// <summary>One resolution: the passphrase (null = unencrypted) and the source it came from.</summary>
public sealed record ResolvedKey(string? Passphrase, string SourceName)
{
    public static readonly ResolvedKey None = new(null, "");
}
