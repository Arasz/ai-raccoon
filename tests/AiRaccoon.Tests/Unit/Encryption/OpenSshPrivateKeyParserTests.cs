using System.Text;
using AiRaccoon.Core.Encryption;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     RFC 8709 openssh-key-v1 format decoding for unencrypted ed25519 private keys
///     (plan §5.1): ciphername "none" only, ssh-ed25519 only, checkint pair, 64-byte
///     private field whose embedded public half matches the public key blob.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OpenSshPrivateKeyParserTests
{
    // Randomly generated test fixture, never used as a real secret.
    private const string RealKeyPem = """
                                      -----BEGIN OPENSSH PRIVATE KEY-----
                                      b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
                                      QyNTUxOQAAACCq0GckZdGdBfT9GruIF54nfPj7YJBaTHHgGQCO5OCmygAAAIinuAM0p7gD
                                      NAAAAAtzc2gtZWQyNTUxOQAAACCq0GckZdGdBfT9GruIF54nfPj7YJBaTHHgGQCO5OCmyg
                                      AAAEBMjrtOXMwX3QeeWNxOFgB50ioPx660+4icJtYSvttC6qrQZyRl0Z0F9P0au4gXnid8
                                      +PtgkFpMceAZAI7k4KbKAAAAAAECAwQF
                                      -----END OPENSSH PRIVATE KEY-----
                                      """;

    private static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly byte[] PublicKey01To20 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly byte[] PublicKey21To40 = [.. Enumerable.Range(33, 32).Select(i => (byte)i)];

    public static TheoryData<string> MalformedPemCases =>
    [
        new OpenSshKeyBuilder().WithBadMagic().Build(),
        new OpenSshKeyBuilder().WithTruncatedBody().Build(),
        new OpenSshKeyBuilder().WithMismatchedCheckints().Build(),
        new OpenSshKeyBuilder().WithEmbeddedPublicKeyMismatch().Build(),
        new OpenSshKeyBuilder().WithInvalidBase64().Build()
    ];

    [Fact]
    public void ParseSeed_SyntheticEd25519Key_ReturnsTheSeed()
    {
        var pem = new OpenSshKeyBuilder().Build();

        var seed = OpenSshPrivateKeyParser.ParseSeed(pem);

        seed.ShouldBe(Seed00To1F);
    }

    [Fact]
    public void ParseSeed_EncryptedKey_ThrowsPassphraseProtected()
    {
        var pem = new OpenSshKeyBuilder().WithEncrypted().Build();

        var ex = Should.Throw<PassphraseProtectedKeyException>(() => OpenSshPrivateKeyParser.ParseSeed(pem));

        ex.Message.ShouldBe("passphrase-protected keys are not supported");
    }

    [Fact]
    public void ParseSeed_RsaKey_ThrowsUnsupportedKeyType()
    {
        var pem = new OpenSshKeyBuilder().WithKeyType("ssh-rsa").Build();

        var ex = Should.Throw<UnsupportedKeyTypeException>(() => OpenSshPrivateKeyParser.ParseSeed(pem));

        ex.Message.ShouldBe("only ed25519 keys are supported");
    }

    [Theory]
    [MemberData(nameof(MalformedPemCases))]
    public void ParseSeed_MalformedKey_ThrowsMalformedWithDetail(string pem)
    {
        var ex = Should.Throw<MalformedPrivateKeyException>(() => OpenSshPrivateKeyParser.ParseSeed(pem));

        ex.Message.ShouldStartWith("malformed OpenSSH private key: ");
    }

    [Fact]
    public void ParseSeed_RealSshKeygenEd25519Key_ReturnsSeedAndDerivesPinnedKey()
    {
        // Throwaway key generated 2026-08-05 with `ssh-keygen -t ed25519 -N '' -C ''` purely as a parser fixture.
        var seed = OpenSshPrivateKeyParser.ParseSeed(RealKeyPem);

        seed.ShouldBe(Convert.FromHexString("4c8ebb4e5ccc17dd079e58dc4e160079d22a0fc7aeb4fb889c26d612bedb42ea"));
        SshKeyDerivation.DeriveRawKey(seed).ShouldBe("x'4ea6b27fdc450764e6727d50599f6c9efd62a367ea044700e5377fa230330427'");
    }

    /// <summary>Assembles an openssh-key-v1 blob from synthetic bytes — deterministic, no real key material.</summary>
    private sealed class OpenSshKeyBuilder
    {
        private uint _checkint1 = 0x01234567;
        private uint _checkint2 = 0x01234567;
        private string _cipherName = "none";
        private bool _invalidBase64;
        private string _kdfName = "none";
        private string _keyType = "ssh-ed25519";
        private string _magic = "openssh-key-v1\0";
        private byte[] _privatePublicKey = PublicKey01To20;
        private byte[] _publicKey = PublicKey01To20;
        private bool _truncateBase64;

        public OpenSshKeyBuilder WithBadMagic()
        {
            _magic = "openssh-key-v9\0";
            return this;
        }

        public OpenSshKeyBuilder WithEncrypted(string cipherName = "aes256-ctr", string kdfName = "bcrypt")
        {
            _cipherName = cipherName;
            _kdfName = kdfName;
            return this;
        }

        public OpenSshKeyBuilder WithKeyType(string keyType)
        {
            _keyType = keyType;
            return this;
        }

        public OpenSshKeyBuilder WithMismatchedCheckints()
        {
            _checkint2 = 0x89abcdef;
            return this;
        }

        public OpenSshKeyBuilder WithEmbeddedPublicKeyMismatch()
        {
            _privatePublicKey = PublicKey21To40;
            return this;
        }

        public OpenSshKeyBuilder WithTruncatedBody()
        {
            _truncateBase64 = true;
            return this;
        }

        public OpenSshKeyBuilder WithInvalidBase64()
        {
            _invalidBase64 = true;
            return this;
        }

        public string Build()
        {
            using var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(_magic));
            WriteString(body, _cipherName);
            WriteString(body, _kdfName);
            WriteString(body, []);
            WriteUInt32(body, 1);
            WriteString(body, BuildPublicKeyBlob());
            WriteString(body, BuildPrivateSection());

            var base64 = Convert.ToBase64String(body.ToArray());
            if (_invalidBase64)
            {
                base64 = "!!!not-base64!!!";
            }
            else if (_truncateBase64)
            {
                base64 = base64[..^16];
            }

            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + base64 + "\n-----END OPENSSH PRIVATE KEY-----\n";
        }

        private byte[] BuildPublicKeyBlob()
        {
            using var blob = new MemoryStream();
            WriteString(blob, _keyType);
            WriteString(blob, _publicKey);
            return blob.ToArray();
        }

        private byte[] BuildPrivateSection()
        {
            using var section = new MemoryStream();
            WriteUInt32(section, _checkint1);
            WriteUInt32(section, _checkint2);
            WriteString(section, _keyType);
            WriteString(section, _publicKey);
            WriteString(section, [.. Seed00To1F, .. _privatePublicKey]);
            WriteString(section, []);
            section.Write(new byte[8 - (int)section.Length % 8]);
            return section.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteString(Stream stream, byte[] value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }
    }
}
