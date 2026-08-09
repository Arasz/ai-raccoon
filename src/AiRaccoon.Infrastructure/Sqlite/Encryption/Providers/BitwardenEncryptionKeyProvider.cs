using System.Security.Cryptography;
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
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    public const string EncryptionSource = "bitwarden";

    public string Source => EncryptionSource;

    public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

    public Passphrase GetPassphrase(EncryptionData encryptionData)
    {
        Guard.IsNotNullOrWhiteSpace(encryptionData.SecretId);

        var result = cliSecretManager.Run(["secret", "get", encryptionData.SecretId], null, FetchTimeout);

        if (result.ExitCode != 0)
        {
            throw new BwsInvocationException($"bws failed (exit {result.ExitCode}): {result.FirstErrorLine}");
        }

        var seed = OpenSshPrivateKeyParser.ParseSeed(result.Stdout.Trim());
        var (value, legacyValue) = DeriveAndZeroSeed(seed);
        return new Passphrase(Source)
        {
            Value = value,
            LegacyValue = legacyValue
        };
    }

    /// <summary>Derives both keys from the seed, then zeroes it — the seed must not outlive this call.</summary>
    internal static (string Value, string LegacyValue) DeriveAndZeroSeed(byte[] seed)
    {
        try
        {
            return (SshKeyDerivation.DeriveRawKey(seed), SshKeyDerivation.DeriveLegacyRawKey(seed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
