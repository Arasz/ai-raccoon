using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

/// <summary>
///     Fetches the bank key from Bitwarden: `bws secret get &lt;secretId&gt;` (no -t, 15 s), parses
///     the ed25519 secret with the Core parser, derives the x'…' raw key (plan §5.1/§5.3/§5.4).
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
            throw new BwsInvocationException($"bws failed (exit {result.ExitCode}): {FirstStderrLine(result.Stderr)}");
        }

        var seed = OpenSshPrivateKeyParser.ParseSeed(result.Stdout.Trim());
        return new Passphrase(Source)
        {
            Value = SshKeyDerivation.DeriveRawKey(seed)
        };
    }

    private static string FirstStderrLine(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? "(no stderr)";
    }
}
