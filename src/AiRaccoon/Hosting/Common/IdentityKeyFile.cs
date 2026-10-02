using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The per-root ECDSA P-256 trust anchor, kept as a 0600 PKCS#8 PEM in the bank state directory
///     (ADR-0106 D1). Only `serve` mints or heals it; a verifier reads it and creates nothing — a
///     missing key is "cannot attach", never "mint one here".
/// </summary>
public sealed class IdentityKeyFile : OwnerOnlySecretFile<ECDsa>
{
    public const string FileName = "identity-key";

    private const int KeySize = 256;

    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    private static readonly SemaphoreSlim KeyGate = new(1, 1);

    private ECDsa? _signer;

    public IdentityKeyFile(InfrastructureOptions options, TimeProvider? timeProvider = null, TimeSpan? healAfter = null)
        : base(options, FileName, timeProvider, healAfter)
    {
    }

    protected override SemaphoreSlim Gate => KeyGate;

    /// <summary>The keyId the stored key would sign as; null exactly when <see cref="OwnerOnlySecretFile{T}.Read" /> is null.</summary>
    public string? ReadKeyId() => Read() is { } key ? IdentityProof.KeyId(key) : null;

    /// <summary>The lock a concurrent mint/heal takes; exposed for the cross-process convergence gate.</summary>
    internal static string LockPathFor(string stateDirectory) =>
        OwnerOnlyFile.LockPathFor(System.IO.Path.Combine(stateDirectory, FileName));

    protected override ECDsa Mint() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    protected override string Serialize(ECDsa secret) => secret.ExportPkcs8PrivateKeyPem();

    /// <summary>The signer is imported once per instance, never re-imported per read or request.</summary>
    protected override void Minted(ECDsa secret) => _signer = secret;

    protected override ECDsa? ReadStateFile() => _signer ??= base.ReadStateFile();

    /// <summary>The key in <paramref name="content" />, or null with a named refusal when it is not a usable P-256 private key.</summary>
    protected override ECDsa? Parse(string content, out string? refusal)
    {
        refusal = null;
        try
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(content);
                var parameters = key.ExportParameters(false);
                if (key.KeySize != KeySize || !string.Equals(parameters.Curve.Oid.Value, NistP256Oid, StringComparison.Ordinal))
                {
                    refusal = $"'{Path}' is not an ECDSA P-256 identity key — remove it and start serve again to mint one";
                    key.Dispose();
                    return null;
                }

                // A public-only PEM imports fine; prove a private key exists before trusting it.
                _ = key.ExportPkcs8PrivateKey();
            }
            catch
            {
                key.Dispose();
                throw;
            }

            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
