using System.Security.Cryptography;
using System.Text;

namespace AiRaccoon.Infrastructure.Sync;

/// <inheritdoc cref="ISyncBlobAuthenticator" />
public sealed class SyncBlobAuthenticator : ISyncBlobAuthenticator
{
    // Deliberately not "SQLite format 3\0" — recognizably distinct so a legacy, never-wrapped
    // blob can never be mistaken for a wrapped one.
    private static readonly byte[] Header = "AIRACCOON-SYNC-HMAC-V1\n"u8.ToArray();
    private static readonly byte[] KeyInfo = "ai-raccoon-sync-auth"u8.ToArray();
    private const int TagLength = 32; // HMACSHA256 output size

    public byte[] Wrap(string passphrase, byte[] data)
    {
        var tag = ComputeTag(passphrase, data);
        var wrapped = new byte[Header.Length + TagLength + data.Length];
        Header.CopyTo(wrapped, 0);
        tag.CopyTo(wrapped, Header.Length);
        data.CopyTo(wrapped, Header.Length + TagLength);
        return wrapped;
    }

    public bool TryUnwrap(byte[] blob, out byte[] tag, out byte[] data)
    {
        if (blob.Length >= Header.Length + TagLength && blob.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            tag = blob[Header.Length..(Header.Length + TagLength)];
            data = blob[(Header.Length + TagLength)..];
            return true;
        }

        tag = [];
        data = blob;
        return false;
    }

    public bool Verify(string passphrase, byte[] tag, byte[] data) =>
        CryptographicOperations.FixedTimeEquals(ComputeTag(passphrase, data), tag);

    /// <summary>Derives a purpose-specific key from the passphrase via HKDF (platform primitive,
    /// not a hand-rolled scheme) and tags the data with HMAC-SHA256.</summary>
    private static byte[] ComputeTag(string passphrase, byte[] data)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(passphrase), outputLength: 32,
            info: KeyInfo);
        return HMACSHA256.HashData(key, data);
    }
}
