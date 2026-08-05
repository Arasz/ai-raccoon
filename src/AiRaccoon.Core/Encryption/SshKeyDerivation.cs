using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Encryption;

/// <summary>
///     Measured derivation for the memory bank: raw = SHA-256(Label ‖ seed), formatted as
///     x'&lt;64 lowercase hex&gt;' — the exact string the SQLCipher Password channel expects (plan §5.1).
/// </summary>
public static class SshKeyDerivation
{
    /// <summary>Domain-separation label; part of the derived key — changing it silently breaks existing banks.</summary>
    public const string Label = "ai-raccoon-db-key/v1";

    private const int SeedLength = 32;
    private static readonly byte[] LabelBytes = Encoding.UTF8.GetBytes(Label);

    public static string DeriveRawKey(ReadOnlySpan<byte> seed)
    {
        Guard.HasSizeEqualTo(seed, SeedLength, nameof(seed));

        Span<byte> input = stackalloc byte[LabelBytes.Length + seed.Length];
        LabelBytes.CopyTo(input);
        seed.CopyTo(input[LabelBytes.Length..]);

        return "x'" + Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() + "'";
    }
}
