using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

/// <summary>
///     Fetches the bank key from Bitwarden: `bws secret get &lt;secretId&gt;` (no -t, 15 s), parses
///     the ed25519 secret with the Core parser, derives the x'…' raw key
///     (docs/plans/encryption-bitwarden-implementation.md §5.1/§5.3/§5.4).
/// </summary>
public sealed class BitwardenEncryptionKeyProvider(ICliSecretManager cliSecretManager) : IEncryptionKeyProvider
{
    public const string EncryptionSource = "bitwarden";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    public string Source => EncryptionSource;

    public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

    public async Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encryptionData.SecretId))
        {
            throw new EncryptionSourceException("the encryption source is bitwarden but names no secret id — run 'ai-raccoon encryption bitwarden' again");
        }

        var result = await cliSecretManager.RunAsync(["secret", "get", encryptionData.SecretId], null, FetchTimeout,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new CliSecretInvocationException(CliSecretFailure.Failed, $"bws failed (exit {result.ExitCode}): {result.FirstErrorLine}");
        }

        var keys = SshKeyPassphrase.FromOpenSsh(result.Stdout);
        return new Passphrase(Source)
        {
            Value = keys.Value,
            LegacyValue = keys.LegacyValue
        };
    }
}
