using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

/// <summary>
///     Fetches the bank key from 1Password: <c>op read &lt;reference&gt;</c> (no token argument, 30 s),
///     parses the SSH Key item's OpenSSH ed25519 key and derives the x'…' raw key (ADR-0132).
///     No legacy derivation exists for this source.
/// </summary>
public sealed class OnePasswordEncryptionKeyProvider(ICliSecretManager cliSecretManager) : IEncryptionKeyProvider
{
    public const string EncryptionSource = "1password";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public string Source => EncryptionSource;

    public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

    public async Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(encryptionData.SecretId))
        {
            throw new EncryptionSourceException("the encryption source is 1password but names no secret reference — run 'ai-raccoon encryption 1password' again");
        }

        var result = await cliSecretManager.RunAsync(["read", encryptionData.SecretId], null, FetchTimeout, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new CliSecretInvocationException(CliSecretFailure.Failed, $"op failed (exit {result.ExitCode}): {result.FirstErrorLine}");
        }

        return new Passphrase(Source) { Value = SshKeyPassphrase.FromOpenSsh(result.Stdout).Value };
    }
}
