using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Encryption;

/// <summary>
///     Format decoder for unencrypted ed25519 OpenSSH private keys (RFC 8709 openssh-key-v1):
///     extracts the 32-byte ed25519 seed. Format decoding only — no crypto (plan §5.1).
/// </summary>
public static class OpenSshPrivateKeyParser
{
    private const string BeginFrame = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string EndFrame = "-----END OPENSSH PRIVATE KEY-----";
    private static readonly byte[] Magic = [.. "openssh-key-v1\0"u8];
    private static readonly byte[] Ed25519 = [.. "ssh-ed25519"u8];
    private static readonly byte[] None = [.. "none"u8];

    public static byte[] ParseSeed(string pem)
    {
        Guard.IsNotNull(pem);
        return ParseContainer(DecodePem(pem));
    }

    private static byte[] DecodePem(string pem)
    {
        var start = pem.IndexOf(BeginFrame, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new MalformedPrivateKeyException("missing PEM begin frame");
        }

        start += BeginFrame.Length;
        var end = pem.IndexOf(EndFrame, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new MalformedPrivateKeyException("missing PEM end frame");
        }

        var compact = string.Concat(pem[start..end].Where(c => !char.IsWhiteSpace(c)));
        try
        {
            return Convert.FromBase64String(compact);
        }
        catch (FormatException)
        {
            throw new MalformedPrivateKeyException("invalid base64 body");
        }
    }

    private static byte[] ParseContainer(byte[] blob)
    {
        var reader = new BlobReader(blob);

        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new MalformedPrivateKeyException("invalid magic — expected 'openssh-key-v1'");
        }

        if (!reader.ReadString().SequenceEqual(None) || !reader.ReadString().SequenceEqual(None))
        {
            // ciphername ≠ "none" (e.g. aes256-ctr with bcrypt KDF) means a passphrase-protected key.
            throw new PassphraseProtectedKeyException();
        }

        reader.ReadString(); // kdfoptions — unused for cipher "none"

        if (reader.ReadUInt32() != 1)
        {
            throw new MalformedPrivateKeyException("expected exactly one key");
        }

        var publicKey = ParsePublicKeyBlob(reader.ReadString());

        var privateSection = reader.ReadString();
        if (reader.Remaining != 0)
        {
            throw new MalformedPrivateKeyException("trailing data after private key section");
        }

        return ParsePrivateSection(privateSection, publicKey);
    }

    private static ReadOnlySpan<byte> ParsePublicKeyBlob(ReadOnlySpan<byte> blob)
    {
        var reader = new BlobReader(blob);

        if (!reader.ReadString().SequenceEqual(Ed25519))
        {
            throw new UnsupportedKeyTypeException();
        }

        var key = reader.ReadString();
        if (key.Length != 32)
        {
            throw new MalformedPrivateKeyException("public key must be 32 bytes");
        }

        if (reader.Remaining != 0)
        {
            throw new MalformedPrivateKeyException("unexpected trailing data in public key blob");
        }

        return key;
    }

    private static byte[] ParsePrivateSection(ReadOnlySpan<byte> section, ReadOnlySpan<byte> expectedPublicKey)
    {
        var reader = new BlobReader(section);

        var checkint1 = reader.ReadUInt32();
        var checkint2 = reader.ReadUInt32();
        if (checkint1 != checkint2)
        {
            throw new MalformedPrivateKeyException("checkint mismatch");
        }

        if (!reader.ReadString().SequenceEqual(Ed25519))
        {
            throw new UnsupportedKeyTypeException();
        }

        reader.ReadString(); // public key — redundant copy; the authoritative check is the private field below

        var privateKey = reader.ReadString();
        if (privateKey.Length != 64)
        {
            throw new MalformedPrivateKeyException("private key field must be 64 bytes");
        }

        reader.ReadString(); // comment

        var padding = reader.Remaining;
        if (padding is < 1 or > 8 || section.Length % 8 != 0)
        {
            throw new MalformedPrivateKeyException("invalid padding");
        }

        if (!privateKey[32..].SequenceEqual(expectedPublicKey))
        {
            throw new MalformedPrivateKeyException("embedded public key does not match the public key field");
        }

        return [.. privateKey[..32]];
    }

    /// <summary>Big-endian field reader over an openssh-key-v1 blob; any overrun is a malformed key.</summary>
    private ref struct BlobReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > _data.Length - _offset)
            {
                throw new MalformedPrivateKeyException("unexpected end of data");
            }

            var slice = _data.Slice(_offset, count);
            _offset += count;
            return slice;
        }

        public uint ReadUInt32()
        {
            var bytes = ReadBytes(4);
            return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        }

        public ReadOnlySpan<byte> ReadString()
        {
            var length = ReadUInt32();
            return ReadBytes((int)length);
        }

        public int Remaining => _data.Length - _offset;
    }
}
