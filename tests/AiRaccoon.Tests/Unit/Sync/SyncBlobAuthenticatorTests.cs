using System.Text;
using AiRaccoon.Infrastructure.Sync;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

/// <summary>
///     The HKDF+HMAC composition extracted out of SyncService: embeds an authenticity tag directly
///     in the blob bytes (no separate sidecar object to tear from the blob it authenticates) and
///     verifies it against a passphrase-derived key. Narrows NoHandRolledCryptoTests' raw-primitive
///     allowlist to this one small file instead of all of SyncService.cs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncBlobAuthenticatorTests
{
    private static readonly byte[] SampleData = Encoding.UTF8.GetBytes("pretend this is a VACUUM INTO snapshot");

    private readonly SyncBlobAuthenticator _authenticator = new();

    [Fact]
    public void Wrap_ThenTryUnwrap_RoundTripsTheOriginalData()
    {
        var wrapped = _authenticator.Wrap("correct-passphrase", SampleData);

        _authenticator.TryUnwrap(wrapped, out var tag, out var data).ShouldBeTrue();
        data.ShouldBe(SampleData);
        tag.Length.ShouldBe(32, "HMACSHA256 produces a 32-byte tag");
    }

    [Fact]
    public void TryUnwrap_ReturnsFalse_ForDataWithNoHeader()
    {
        _authenticator.TryUnwrap(SampleData, out _, out var data).ShouldBeFalse();
        data.ShouldBe(SampleData, "an unwrapped legacy blob must be returned unchanged");
    }

    [Fact]
    public void TryUnwrap_ReturnsFalse_ForDataShorterThanTheHeaderAndTag()
    {
        _authenticator.TryUnwrap([1, 2, 3], out _, out var data).ShouldBeFalse();
        data.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Verify_ReturnsTrue_WhenTheTagMatchesTheDataUnderTheSamePassphrase()
    {
        var wrapped = _authenticator.Wrap("correct-passphrase", SampleData);
        _authenticator.TryUnwrap(wrapped, out var tag, out var data).ShouldBeTrue();

        _authenticator.Verify("correct-passphrase", tag, data).ShouldBeTrue();
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenTheDataWasTamperedAfterWrapping()
    {
        var wrapped = _authenticator.Wrap("correct-passphrase", SampleData);
        _authenticator.TryUnwrap(wrapped, out var tag, out var data).ShouldBeTrue();

        var tampered = (byte[])data.Clone();
        tampered[0] ^= 0xFF;

        _authenticator.Verify("correct-passphrase", tag, tampered).ShouldBeFalse();
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTheWrongPassphrase()
    {
        var wrapped = _authenticator.Wrap("correct-passphrase", SampleData);
        _authenticator.TryUnwrap(wrapped, out var tag, out var data).ShouldBeTrue();

        _authenticator.Verify("wrong-passphrase", tag, data).ShouldBeFalse();
    }

    [Fact]
    public void Wrap_ProducesDifferentBytesThanTheRawSqliteMagic()
    {
        // The wrapped header must never be mistaken for "SQLite format 3\0" — a wrapped blob is
        // stripped before the file SQLite itself opens, never handed to it directly.
        var wrapped = _authenticator.Wrap("correct-passphrase", SampleData);
        var sqliteMagic = "SQLite format 3\0"u8.ToArray();

        wrapped.AsSpan(0, sqliteMagic.Length).SequenceEqual(sqliteMagic).ShouldBeFalse();
    }
}
