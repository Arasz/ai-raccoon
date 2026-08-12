using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Encryption;

/// <summary>
///     Derives the memory bank's SQLCipher raw key from an ed25519 seed via HKDF-SHA-256, formatted as
///     x'&lt;64 lowercase hex&gt; — the string the Password channel expects. See ADR-0012.
/// </summary>
public static class SshKeyDerivation
{
    /// <summary>Domain-separation label; part of the derived key — changing it silently breaks existing banks.</summary>
    public const string Label = "ai-raccoon-db-key/v1";

    private const int SeedLength = 32;
    private const int KeyLength = 32;
    private static readonly byte[] LabelBytes = Encoding.UTF8.GetBytes(Label);

    public static string DeriveRawKey(ReadOnlySpan<byte> seed)
    {
        Guard.HasSizeEqualTo(seed, SeedLength);

        Span<byte> key = stackalloc byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, key, default, LabelBytes);

        return Format(key);
    }

    /// <summary>
    ///     The pre-ADR-0012 derivation, SHA-256(Label ‖ seed). Retained only to open banks encrypted
    ///     before the change so they can be rekeyed; never use it for a new bank.
    /// </summary>
    public static string DeriveLegacyRawKey(ReadOnlySpan<byte> seed)
    {
        Guard.HasSizeEqualTo(seed, SeedLength);

        Span<byte> input = stackalloc byte[LabelBytes.Length + seed.Length];
        LabelBytes.CopyTo(input);
        seed.CopyTo(input[LabelBytes.Length..]);

        Span<byte> key = stackalloc byte[KeyLength];
        SHA256.HashData(input, key);

        return Format(key);
    }

    private static string Format(ReadOnlySpan<byte> key) => string.Concat("x'", Convert.ToHexString(key).ToLowerInvariant(), "'");
}
