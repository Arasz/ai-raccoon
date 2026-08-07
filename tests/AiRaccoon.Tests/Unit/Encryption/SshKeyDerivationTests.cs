using System.Security.Cryptography;
using AiRaccoon.Core.Encryption;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     Pins the derivation contract (ADR-0012): raw = HKDF-SHA-256(ikm: seed, info: "ai-raccoon-db-key/v1"),
///     formatted as x'&lt;64 lowercase hex&gt;'. The legacy SHA-256(label ‖ seed) vectors are pinned too — banks
///     encrypted before ADR-0012 are opened with them and rekeyed. Vectors hard-coded, never recomputed here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SshKeyDerivationTests
{
    private static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    [Fact]
    public void DeriveRawKey_SyntheticSeed00To1F_ReturnsPinnedVector()
    {
        var key = SshKeyDerivation.DeriveRawKey(Seed00To1F);

        key.ShouldBe("x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'");
    }

    [Fact]
    public void DeriveRawKey_RealSshKeygenSeed_ReturnsPinnedVector()
    {
        var seed = Convert.FromHexString("6868227276d58fd3a3c67be90bad5cb2cc53ee5f46ec3e03ba483b1eaff2d7e0");

        var key = SshKeyDerivation.DeriveRawKey(seed);

        key.ShouldBe("x'ab4b0145bfaefc0a323267391971ab2b1540cbd546494171d9cdadf5f973a985'");
    }

    [Fact]
    public void DeriveRawKey_ReturnsLowercaseHexInsideXQuotes()
    {
        var key = SshKeyDerivation.DeriveRawKey(Seed00To1F);

        key.ShouldMatch(@"^x'[0-9a-f]{64}'$");
    }

    [Fact]
    public void DeriveRawKey_DifferentLabel_ProducesADifferentKey()
    {
        // Pins the stability contract: the label is part of the preimage, so the v1 output
        // must differ from the same seed hashed with any other label.
        var v2Input = "ai-raccoon-db-key/v2"u8.ToArray().Concat(Seed00To1F).ToArray();
        var v2Key = $"x'{Convert.ToHexString(SHA256.HashData(v2Input)).ToLowerInvariant()}'";

        var key = SshKeyDerivation.DeriveRawKey(Seed00To1F);

        SshKeyDerivation.Label.ShouldBe("ai-raccoon-db-key/v1");
        key.ShouldNotBe(v2Key);
    }

    [Fact]
    public void DeriveRawKey_SeedNotExactly32Bytes_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => SshKeyDerivation.DeriveRawKey(new byte[16]));

        ex.ParamName.ShouldBe("seed");
    }

    [Fact]
    public void DeriveLegacyRawKey_SyntheticSeed00To1F_ReturnsPreAdr0012Vector()
    {
        var key = SshKeyDerivation.DeriveLegacyRawKey(Seed00To1F);

        key.ShouldBe("x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'");
    }

    [Fact]
    public void DeriveLegacyRawKey_RealSshKeygenSeed_ReturnsPreAdr0012Vector()
    {
        var seed = Convert.FromHexString("6868227276d58fd3a3c67be90bad5cb2cc53ee5f46ec3e03ba483b1eaff2d7e0");

        var key = SshKeyDerivation.DeriveLegacyRawKey(seed);

        key.ShouldBe("x'5944055840d8941e4cb6d5bb0dedfb3e1808bb7b33727107de0e9399054ee83d'");
    }

    [Fact]
    public void DeriveRawKey_DiffersFromLegacy_SoExistingBanksNeedARekey()
    {
        SshKeyDerivation.DeriveRawKey(Seed00To1F)
            .ShouldNotBe(SshKeyDerivation.DeriveLegacyRawKey(Seed00To1F));
    }
}
