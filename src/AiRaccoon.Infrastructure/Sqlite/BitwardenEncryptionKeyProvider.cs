using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Fetches the bank key from Bitwarden: `bws secret get &lt;secretId&gt;` (no -t, 15 s), parses
///     the ed25519 secret with the Core parser, derives the x'…' raw key (plan §5.1/§5.3/§5.4).
/// </summary>
public sealed class BitwardenEncryptionKeyProvider : IEncryptionKeyProvider
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(15);

    private readonly IBwsProcessRunner _runner;
    private readonly string _secretId;

    public BitwardenEncryptionKeyProvider(IBwsProcessRunner runner, string secretId)
    {
        Guard.IsNotNull(runner);
        Guard.IsNotNullOrWhiteSpace(secretId);
        _runner = runner;
        _secretId = secretId;
    }

    public string GetPassphrase()
    {
        BwsResult result;
        try
        {
            // No -t: the token comes from BWS_ACCESS_TOKEN inherited by the child process.
            result = _runner.Run(["secret", "get", _secretId], token: null, FetchTimeout);
        }
        catch (BwsInvocationException)
        {
            throw; // Already carries §5.4 text (not found / timed out / no output).
        }

        if (result.ExitCode != 0)
        {
            throw new BwsInvocationException($"bws failed (exit {result.ExitCode}): {FirstStderrLine(result.Stderr)}");
        }

        var seed = OpenSshPrivateKeyParser.ParseSeed(result.Stdout.Trim());
        return SshKeyDerivation.DeriveRawKey(seed);
    }

    private static string FirstStderrLine(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line ?? "(no stderr)";
    }
}
