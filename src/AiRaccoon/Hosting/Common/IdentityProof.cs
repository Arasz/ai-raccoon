using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Common;

/// <summary>Why a listener's identity did not prove; the wire maps every one to "not trusted".</summary>
public enum IdentityProofFailure
{
    /// <summary>This root holds no identity key — the verifier never mints one.</summary>
    NoKey,

    /// <summary>The listener serves a different root (its fingerprint differs from the challenge's).</summary>
    RootMismatch,

    /// <summary>The signature does not verify under this root's key, or the keyId is not this key's.</summary>
    BadSignature,

    /// <summary>The response was not the frozen shape (missing field, undecodable signature).</summary>
    Malformed,

    /// <summary>The listener answered with a status other than 200.</summary>
    NonSuccessStatus,

    /// <summary>The listener did not answer inside the proof budget.</summary>
    Timeout
}

/// <summary>
///     The frozen identity-proof wire v1 (ADR-0106): a domain-separated transcript over the nonce,
///     keyId, root fingerprint and the dialled port, signed as SHA-256 with ECDSA IEEE-P1363
///     fixed-field concatenation. Pure by design — no file or network I/O.
/// </summary>
public static class IdentityProof
{
    public const string DomainLabel = "ai-raccoon/identity/v1";

    /// <summary>The one pre-token route, on the existing loopback listener (ADR-0106 D2).</summary>
    public const string EndpointPath = "/identity/prove";

    public const int NonceBytes = 32;

    /// <summary>P-256's r‖s is always 32 + 32 bytes.</summary>
    public const int SignatureLength = 64;

    /// <summary>A fresh CSPRNG nonce per request attempt; replay dies here.</summary>
    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>base64url(SHA-256(SPKI DER)) — the key the listener may sign with.</summary>
    public static string KeyId(ECDsa key)
    {
        Guard.IsNotNull(key);
        return Base64Url.EncodeToString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>
    ///     base64url(SHA-256(canonical path)) — canonicalized by <see cref="Path.GetFullPath(string)" />
    ///     with the trailing separator trimmed, so a copied state directory restored at another root
    ///     cannot pass as this one.
    /// </summary>
    public static string RootFingerprint(string stateDirectory)
    {
        Guard.IsNotNullOrWhiteSpace(stateDirectory);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The exact frozen transcript: label, nonce, keyId, rootFp and the dialled port.</summary>
    public static byte[] Transcript(string nonce, string keyId, string rootFp, int port)
    {
        Guard.IsNotNullOrWhiteSpace(nonce);
        Guard.IsNotNullOrWhiteSpace(keyId);
        Guard.IsNotNullOrWhiteSpace(rootFp);
        return Encoding.UTF8.GetBytes(
            $"{DomainLabel}\n{nonce}\n{keyId}\n{rootFp}\n{port.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Signs the transcript under <paramref name="signer" /> in the frozen P1363 encoding.</summary>
    public static string Sign(ECDsa signer, string nonce, string keyId, string rootFp, int port)
    {
        Guard.IsNotNull(signer);
        var hash = SHA256.HashData(Transcript(nonce, keyId, rootFp, port));
        return Base64Url.EncodeToString(
            signer.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>
    ///     Verifies a listener's answer against this root's trust anchor. Null means proven; a value
    ///     names why not. <paramref name="trustedKey" /> null is <see cref="IdentityProofFailure.NoKey" />
    ///     — the verifier never mints, it only reports.
    /// </summary>
    public static IdentityProofFailure? Verify(ECDsa? trustedKey, string nonce, string rootFp, int port,
        string? responseKeyId, string? responseSignature)
    {
        if (trustedKey is null)
        {
            return IdentityProofFailure.NoKey;
        }

        if (responseKeyId is null || responseSignature is null)
        {
            return IdentityProofFailure.Malformed;
        }

        if (!string.Equals(responseKeyId, KeyId(trustedKey), StringComparison.Ordinal))
        {
            return IdentityProofFailure.BadSignature;
        }

        if (!Base64Url.IsValid(responseSignature))
        {
            return IdentityProofFailure.Malformed;
        }

        var signature = Base64Url.DecodeFromChars(responseSignature);
        if (signature.Length != SignatureLength)
        {
            return IdentityProofFailure.Malformed;
        }

        var hash = SHA256.HashData(Transcript(nonce, responseKeyId, rootFp, port));
        return trustedKey.VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? null
            : IdentityProofFailure.BadSignature;
    }

    /// <summary>
    ///     The operator-facing clause for a proof <paramref name="failure" /> (ADR-0107 PC.3) — never
    ///     the log line (EventId 603/657 stay unchanged), stderr only. <see cref="IdentityProofFailure.NoKey" />
    ///     and <see cref="IdentityProofFailure.NonSuccessStatus" /> each cover two distinct prover
    ///     branches (a local check and a remote answer), so the text names both rather than picking
    ///     one as fact.
    /// </summary>
    public static string RefusalText(IdentityProofFailure failure) => failure switch
    {
        IdentityProofFailure.NoKey =>
            "this data root has no identity key, or the listener reported it has none",
        IdentityProofFailure.RootMismatch =>
            "it serves another data root",
        IdentityProofFailure.BadSignature =>
            "its signature does not match this data root's identity key",
        IdentityProofFailure.Malformed =>
            "its answer was not a valid identity proof",
        IdentityProofFailure.NonSuccessStatus =>
            "it did not answer the identity challenge — an older ai-raccoon build, a non-ai-raccoon listener and a refused connection all look the same",
        IdentityProofFailure.Timeout =>
            "it did not answer the identity challenge in time",
        _ => ThrowHelper.ThrowArgumentOutOfRangeException<string>(nameof(failure))
    };
}
