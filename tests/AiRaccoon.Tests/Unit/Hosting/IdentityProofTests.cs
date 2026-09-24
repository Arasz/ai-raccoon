using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Hosting.Common;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     D2's frozen wire v1 transcript: the exact domain-separated string, a P1363 signature over its
///     SHA-256, a keyId that pins the SPKI, and a root fingerprint that canonicalizes one directory
///     to one value. All pure: the verifier half never touches the file system.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IdentityProofTests
{
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    [Fact]
    public void Transcript_MatchesTheFrozenWireFormat()
    {
        var transcript = IdentityProof.Transcript("NONCE", "KEYID", "ROOTFP", 7721);

        Encoding.UTF8.GetString(transcript).ShouldBe("ai-raccoon/identity/v1\nNONCE\nKEYID\nROOTFP\n7721");
    }

    [Fact]
    public void NewNonce_Is32RandomBytesAsUnpaddedBase64Url()
    {
        var nonce = IdentityProof.NewNonce();

        nonce.ShouldMatch("^[A-Za-z0-9_-]{43}$");
        nonce.ShouldNotBe(IdentityProof.NewNonce());
    }

    [RetryFact]
    public void RootFingerprint_CanonicalizesEquivalentPathsToTheSameValue()
    {
        var directory = TestData.CreateTempRoot("ai-raccoon-root-fingerprint");
        try
        {
            var fingerprint = IdentityProof.RootFingerprint(directory);

            fingerprint.ShouldMatch("^[A-Za-z0-9_-]{43}$");
            IdentityProof.RootFingerprint(Path.Combine(directory, "sub", "..")).ShouldBe(fingerprint);
            IdentityProof.RootFingerprint(directory + Path.DirectorySeparatorChar).ShouldBe(fingerprint);
        }
        finally
        {
            TestData.DeleteTempRoot(directory);
        }
    }

    /// <summary>Positive control: a signature over the frozen transcript verifies under the same key.</summary>
    [RetryFact]
    public void SignedTranscript_RoundTrips_UnderTheSameKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var keyId = IdentityProof.KeyId(key);
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");

        var signature = IdentityProof.Sign(key, nonce, keyId, rootFp, 41111);

        IdentityProof.Verify(key, nonce, rootFp, 41111, keyId, signature).ShouldBeNull();
    }

    [RetryFact]
    public void KeyId_IsBase64UrlSha256OfTheSubjectPublicKeyInfo()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var expected = Base64Url.EncodeToString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

        IdentityProof.KeyId(key).ShouldBe(expected);
        key.ExportParameters(false).Curve.Oid.Value.ShouldBe(NistP256Oid);
    }

    /// <summary>The frozen format pin: IEEE-P1363 fixed-field concatenation, never a DER sequence.</summary>
    [RetryFact]
    public void Signature_IsIeeeP1363FixedFieldConcatenation_NotDer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var keyId = IdentityProof.KeyId(key);
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");
        var hash = SHA256.HashData(IdentityProof.Transcript(nonce, keyId, rootFp, 7721));

        var p1363 = Base64Url.DecodeFromChars(IdentityProof.Sign(key, nonce, keyId, rootFp, 7721));
        var der = key.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence);

        p1363.Length.ShouldBe(64);
        der.Length.ShouldNotBe(64);
        key.VerifyHash(hash, p1363, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).ShouldBeTrue();
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("rootFp")]
    [InlineData("port")]
    public void ATamperedTranscript_IsBadSignature(string field)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var keyId = IdentityProof.KeyId(key);
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");
        var signature = IdentityProof.Sign(key, nonce, keyId, rootFp, 41111);

        var failure = field switch
        {
            "nonce" => IdentityProof.Verify(key, IdentityProof.NewNonce(), rootFp, 41111, keyId, signature),
            "rootFp" => IdentityProof.Verify(key, nonce, IdentityProof.RootFingerprint("/tmp/other"), 41111, keyId,
                signature),
            _ => IdentityProof.Verify(key, nonce, rootFp, 41112, keyId, signature)
        };

        failure.ShouldBe(IdentityProofFailure.BadSignature);
    }

    [RetryFact]
    public void AMismatchedKeyId_IsBadSignature()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");
        var signature = IdentityProof.Sign(attacker, nonce, IdentityProof.KeyId(attacker), rootFp, 41111);

        IdentityProof.Verify(trusted, nonce, rootFp, 41111, IdentityProof.KeyId(attacker), signature)
            .ShouldBe(IdentityProofFailure.BadSignature);
    }

    [RetryFact]
    public void ASignatureFromAnotherKey_IsBadSignature()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");
        var keyId = IdentityProof.KeyId(trusted);
        // The attacker signs the trusted key's identity, but with its own private key.
        var signature = IdentityProof.Sign(attacker, nonce, keyId, rootFp, 41111);

        IdentityProof.Verify(trusted, nonce, rootFp, 41111, keyId, signature).ShouldBe(IdentityProofFailure.BadSignature);
    }

    [Theory]
    [InlineData("not base64url!")]
    [InlineData("AAAA")]
    [InlineData("")]
    public void AMalformedSignature_IsMalformed(string signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = IdentityProof.NewNonce();
        var keyId = IdentityProof.KeyId(key);
        var rootFp = IdentityProof.RootFingerprint("/tmp/root");

        IdentityProof.Verify(key, nonce, rootFp, 41111, keyId, signature).ShouldBe(IdentityProofFailure.Malformed);
    }

    [RetryFact]
    public void AMissingResponseField_IsMalformed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        IdentityProof.Verify(key, IdentityProof.NewNonce(), IdentityProof.RootFingerprint("/tmp/root"), 41111, null, "x")
            .ShouldBe(IdentityProofFailure.Malformed);
        IdentityProof.Verify(key, IdentityProof.NewNonce(), IdentityProof.RootFingerprint("/tmp/root"), 41111, "x", null)
            .ShouldBe(IdentityProofFailure.Malformed);
    }

    /// <summary>
    ///     F12/F39: the verifier is read-only. With no key file its verdict is NoKey and it creates
    ///     zero files — not even the state directory or a lock.
    /// </summary>
    [RetryFact]
    public void Verifier_WithNoKeyFile_IsNotProven_AndCreatesZeroFiles()
    {
        var root = TestData.CreateTempRoot("ai-raccoon-verifier-no-key");
        try
        {
            var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(root));

            var failure = IdentityProof.Verify(keyFile.Read(), IdentityProof.NewNonce(),
                IdentityProof.RootFingerprint(Path.Combine(root, ".ai-raccoon")), 7721, "keyId", "signature");

            failure.ShouldBe(IdentityProofFailure.NoKey);
            Directory.Exists(Path.Combine(root, ".ai-raccoon")).ShouldBeFalse();
            Directory.GetFileSystemEntries(root).ShouldBeEmpty();
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    /// <summary>
    ///     ADR-0107 PC.3: every <see cref="IdentityProofFailure" /> gets its own operator-facing
    ///     clause — collapsing them all to "did not prove" was the defect this replaces. NoKey and
    ///     NonSuccessStatus each cover two prover branches (IdentityProver.cs:74-78/147 and
    ///     :101-106/133-139), so their text names both rather than asserting one as fact.
    /// </summary>
    [Theory]
    [InlineData(IdentityProofFailure.NoKey, "no identity key")]
    [InlineData(IdentityProofFailure.RootMismatch, "another data root")]
    [InlineData(IdentityProofFailure.BadSignature, "signature")]
    [InlineData(IdentityProofFailure.Malformed, "not a valid identity proof")]
    [InlineData(IdentityProofFailure.NonSuccessStatus, "did not answer the identity challenge")]
    [InlineData(IdentityProofFailure.Timeout, "in time")]
    public void RefusalText_NamesTheFailure(IdentityProofFailure failure, string expectedSubstring)
    {
        IdentityProof.RefusalText(failure).ShouldContain(expectedSubstring);
    }

    [Fact]
    public void RefusalText_NeverClaimsTheListenerIsAnOlderServer()
    {
        // MUST4: NonSuccessStatus also covers a refused connection and a non-ai-raccoon listener,
        // so the text may not assert "too old" as fact.
        IdentityProof.RefusalText(IdentityProofFailure.NonSuccessStatus).ShouldNotContain("too old");
    }
}
