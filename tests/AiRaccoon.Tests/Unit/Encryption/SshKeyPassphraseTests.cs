using AiRaccoon.Core.Encryption;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>SshKeyPassphrase: OpenSSH text → seed → both derived keys, seed zeroed. Vectors hard-coded (seed 00 01 … 1f), never recomputed.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SshKeyPassphraseTests
{
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";
    private const string LegacyDerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    [Fact]
    public void FromOpenSsh_ValidKey_ReturnsBothGoldenKeys()
    {
        var keys = SshKeyPassphrase.FromOpenSsh(new TestOpenSshKeyBuilder().Build());

        keys.Value.ShouldBe(DerivedRawKey);
        keys.LegacyValue.ShouldBe(LegacyDerivedRawKey);
    }

    [Fact]
    public void FromOpenSsh_SurroundingWhitespace_StillDerives()
    {
        var keys = SshKeyPassphrase.FromOpenSsh($"\n{new TestOpenSshKeyBuilder().Build()}\n\n");

        keys.Value.ShouldBe(DerivedRawKey);
    }

    [Fact]
    public void FromOpenSsh_NotAKey_ThrowsEncryptionKeyException()
    {
        Should.Throw<EncryptionKeyException>(() => SshKeyPassphrase.FromOpenSsh("not a key at all"));
    }

    /// <summary>The seed is held for the process lifetime otherwise; DeriveAndZeroSeed is the one call site free to clear it.</summary>
    [Fact]
    public void DeriveAndZeroSeed_DerivesBothKeysThenZeroesTheSeed()
    {
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var keys = SshKeyPassphrase.DeriveAndZeroSeed(seed);

        keys.Value.ShouldBe(DerivedRawKey);
        keys.LegacyValue.ShouldBe(LegacyDerivedRawKey);
        seed.ShouldAllBe(b => b == 0);
    }
}
