using System.Text;
using AiRaccoon.Core.Encryption;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     RFC 8709 openssh-key-v1 format decoding for unencrypted ed25519 private keys:
///     ciphername "none" only, ssh-ed25519 only, checkint pair, and a 64-byte private field
///     whose embedded public half matches the public key blob.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OpenSshPrivateKeyParserTests
{
    // Synthetic bytes, not real ssh-keygen output — replaces a genuine ed25519 key once committed
    // here; a pinned vector's point is determinism, not provenance.
    private static readonly byte[] SecondSeed = "FAKE-PARSER-TEST-SEED-NOT-REAL00"u8.ToArray();
    private static readonly byte[] SecondPublicKey = "FAKE-PARSER-TEST-PUBKEY-NOTREAL0"u8.ToArray();

    private static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly byte[] PublicKey01To20 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly byte[] PublicKey21To40 = [.. Enumerable.Range(33, 32).Select(i => (byte)i)];

    public static TheoryData<string> MalformedPemCases =>
    [
        new TestOpenSshKeyBuilder().WithBadMagic().Build(),
        new TestOpenSshKeyBuilder().WithTruncatedBody().Build(),
        new TestOpenSshKeyBuilder().WithMismatchedCheckints().Build(),
        new TestOpenSshKeyBuilder().WithEmbeddedPublicKeyMismatch().Build(),
        new TestOpenSshKeyBuilder().WithInvalidBase64().Build()
    ];

    [Fact]
    public void ParseSeed_SyntheticEd25519Key_ReturnsTheSeed()
    {
        var pem = new TestOpenSshKeyBuilder().Build();

        var seed = OpenSshPrivateKeyParser.ParseSeed(pem);

        seed.ShouldBe(Seed00To1F);
    }

    [Fact]
    public void ParseSeed_EncryptedKey_ThrowsPassphraseProtected()
    {
        var pem = new TestOpenSshKeyBuilder().WithEncrypted().Build();

        var ex = Should.Throw<PassphraseProtectedKeyException>(() => OpenSshPrivateKeyParser.ParseSeed(pem));

        ex.Message.ShouldBe("passphrase-protected keys are not supported");
    }

    [Fact]
    public void ParseSeed_RsaKey_ThrowsUnsupportedKeyType()
    {
        var pem = new TestOpenSshKeyBuilder().WithKeyType("ssh-rsa").Build();

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
    public void ParseSeed_SecondSyntheticEd25519Key_ReturnsSeedAndDerivesPinnedKey()
    {
        var pem = new TestOpenSshKeyBuilder().Build(SecondSeed, SecondPublicKey);

        var seed = OpenSshPrivateKeyParser.ParseSeed(pem);

        seed.ShouldBe(SecondSeed);
        SshKeyDerivation.DeriveRawKey(seed).ShouldBe("x'f8173a40b39ab1f36295c56d52da87200bc1dc20e633042620422c1e1e091fee'");
    }
}
