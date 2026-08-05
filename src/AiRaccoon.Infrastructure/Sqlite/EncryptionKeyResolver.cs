using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Selects the encryption key provider from the memory.db.source sidecar, read fresh on every
///     call (the config commands change it between calls). Absent sidecar or "env" → the env
///     provider; "bitwarden" → bws fetch + derivation; a corrupt sidecar fails loudly (plan §5.2).
/// </summary>
public sealed class EncryptionKeyResolver : IEncryptionKeyProvider
{
    /// <summary>One resolution: the passphrase (null = unencrypted) and the source it came from.</summary>
    public sealed record ResolvedKey(string? Passphrase, string SourceName);

    private readonly InfrastructureOptions _options;
    private readonly IEncryptionKeyProvider _envProvider;
    private readonly IBwsProcessRunner _runner;

    public EncryptionKeyResolver(InfrastructureOptions options, IEncryptionKeyProvider envProvider, IBwsProcessRunner runner)
    {
        Guard.IsNotNull(options);
        Guard.IsNotNull(envProvider);
        Guard.IsNotNull(runner);
        _options = options;
        _envProvider = envProvider;
        _runner = runner;
    }

    public string? GetPassphrase() => Resolve().Passphrase;

    public ResolvedKey Resolve()
    {
        var bankPath = SqliteConnectionFactory.BankPathFor(_options);
        var sidecarPath = EncryptionSourceSidecar.PathFor(bankPath);
        var source = new EncryptionSourceSidecar(bankPath).Read();
        var sourceName = source?.Source ?? EncryptionSettingsKeys.SourceEnv;

        var passphrase = sourceName == EncryptionSettingsKeys.SourceBitwarden
            ? new BitwardenEncryptionKeyProvider(_runner, RequireSecretId(source!, sidecarPath)).GetPassphrase()
            : _envProvider.GetPassphrase();

        return new ResolvedKey(passphrase, sourceName);
    }

    private static string RequireSecretId(EncryptionSourceConfig source, string sidecarPath) =>
        string.IsNullOrWhiteSpace(source.SecretId)
            ? throw new EncryptionSourceException(
                $"encryption source sidecar '{sidecarPath}' is corrupt: source is bitwarden but secretId is missing")
            : source.SecretId;
}
