using System.Text;
using AiRaccoon.Core.Encryption;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

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
    public void ParseSeed_RealSshKeygenEd25519Key_ReturnsSeedAndDerivesPinnedKey()
    {
        // Throwaway key generated 2026-08-05 with `ssh-keygen -t ed25519 -N '' -C ''` purely as a parser fixture.
        var seed = OpenSshPrivateKeyParser.ParseSeed(RealKeyPem);

        seed.ShouldBe(Convert.FromHexString("4c8ebb4e5ccc17dd079e58dc4e160079d22a0fc7aeb4fb889c26d612bedb42ea"));
        SshKeyDerivation.DeriveRawKey(seed).ShouldBe("x'c7374dda8c04f79a6f71197552af1d2ed09e941e9156af13866d366a2466a674'");
    }


}
