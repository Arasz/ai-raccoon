using System.Text;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Assembles a valid openssh-key-v1 PEM blob from synthetic bytes — deterministic, no real key material.
///     The default seed (00..1f) and public key (01..20) produce a reproducible derived key.
/// </summary>
public sealed class TestOpenSshKeyBuilder
{
    public static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    public static readonly byte[] PublicKey01To20 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    public static readonly byte[] PublicKey21To40 = [.. Enumerable.Range(33, 32).Select(i => (byte)i)];

    private byte[]? _privatePublicKeyOverride;
    private uint _checkint1 = 0x01234567;
    private uint _checkint2 = 0x01234567;
    private string _cipherName = "none";
    private string _kdfName = "none";
    private string _keyType = "ssh-ed25519";
    private string _magic = "openssh-key-v1\0";
    private bool _invalidBase64;
    private bool _truncateBase64;

    public TestOpenSshKeyBuilder WithEncrypted(string cipherName = "aes256-ctr", string kdfName = "bcrypt")
    {
        _cipherName = cipherName;
        _kdfName = kdfName;
        return this;
    }

    public TestOpenSshKeyBuilder WithKeyType(string keyType)
    {
        _keyType = keyType;
        return this;
    }

    public TestOpenSshKeyBuilder WithBadMagic()
    {
        _magic = "openssh-key-v9\0";
        return this;
    }

    public TestOpenSshKeyBuilder WithMismatchedCheckints()
    {
        _checkint2 = 0x89abcdef;
        return this;
    }

    public TestOpenSshKeyBuilder WithEmbeddedPublicKeyMismatch()
    {
        _privatePublicKeyOverride = PublicKey21To40;
        return this;
    }

    public TestOpenSshKeyBuilder WithTruncatedBody()
    {
        _truncateBase64 = true;
        return this;
    }

    public TestOpenSshKeyBuilder WithInvalidBase64()
    {
        _invalidBase64 = true;
        return this;
    }

    public string Build(byte[]? seed = null, byte[]? pub = null)
    {
        seed ??= Seed00To1F;
        pub ??= PublicKey01To20;

        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(_magic));
        WriteString(body, _cipherName);
        WriteString(body, _kdfName);
        WriteString(body, []);
        WriteUInt32(body, 1);
        WriteString(body, BuildPublicKeyBlob(pub));
        WriteString(body, BuildPrivateSection(seed, pub));

        var base64 = Convert.ToBase64String(body.ToArray());
        if (_invalidBase64)
        {
            base64 = "!!!not-base64!!!";
        }
        else if (_truncateBase64)
        {
            base64 = base64[..^16];
        }

        if (!_invalidBase64 && !_truncateBase64)
        {
            const int lineLength = 70;
            var wrapped = string.Join('\n', Enumerable.Range(0, (base64.Length + lineLength - 1) / lineLength)
                .Select(i => base64.Substring(i * lineLength, Math.Min(lineLength, base64.Length - i * lineLength))));
            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + wrapped + "\n-----END OPENSSH PRIVATE KEY-----\n";
        }

        return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + base64 + "\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private byte[] BuildPublicKeyBlob(byte[] pub)
    {
        using var blob = new MemoryStream();
        WriteString(blob, _keyType);
        WriteString(blob, pub);
        return blob.ToArray();
    }

    private byte[] BuildPrivateSection(byte[] seed, byte[] pub)
    {
        using var section = new MemoryStream();
        WriteUInt32(section, _checkint1);
        WriteUInt32(section, _checkint2);
        WriteString(section, _keyType);
        WriteString(section, pub);
        WriteString(section, [.. seed, .. (_privatePublicKeyOverride ?? pub)]);
        WriteString(section, []);
        section.Write(new byte[8 - (int)section.Length % 8]);
        return section.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value) =>
        stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void WriteString(Stream stream, string value) =>
        WriteString(stream, Encoding.ASCII.GetBytes(value));

    private static void WriteString(Stream stream, byte[] value)
    {
        WriteUInt32(stream, (uint)value.Length);
        stream.Write(value);
    }
}
