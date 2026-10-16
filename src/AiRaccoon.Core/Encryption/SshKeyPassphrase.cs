using System.Security.Cryptography;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Encryption;

/// <summary>Turns an unencrypted OpenSSH ed25519 key into the bank key pair (current and pre-ADR-0012 derivation), zeroing the seed.</summary>
public static class SshKeyPassphrase
{
    /// <summary>Parses the key text, derives both keys, and zeroes the seed; throws <see cref="EncryptionKeyException" /> for an unusable key.</summary>
    public static DerivedKeys FromOpenSsh(string openSshKey)
    {
        Guard.IsNotNull(openSshKey);
        return DeriveAndZeroSeed(OpenSshPrivateKeyParser.ParseSeed(openSshKey.Trim()));
    }

    /// <summary>Derives both keys from the seed, then zeroes it — the seed must not outlive this call.</summary>
    internal static DerivedKeys DeriveAndZeroSeed(byte[] seed)
    {
        try
        {
            return new DerivedKeys(SshKeyDerivation.DeriveRawKey(seed), SshKeyDerivation.DeriveLegacyRawKey(seed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}

/// <summary>The x'…' raw bank key and the same key under the pre-ADR-0012 derivation.</summary>
public readonly record struct DerivedKeys(string Value, string LegacyValue);
