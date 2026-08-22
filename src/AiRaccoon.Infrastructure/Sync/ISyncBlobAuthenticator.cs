namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Embeds/verifies a sync blob's HMAC authenticity tag directly in the pushed bytes, so
/// publication is atomic under the existing CAS push — there is no separate sidecar object for a
/// torn write, or a delete-only attack, to strip authentication from the blob it protects.</summary>
public interface ISyncBlobAuthenticator
{
    /// <summary>Prepends a magic header and an HMAC-SHA256 tag (keyed via HKDF from <paramref name="passphrase" />) to <paramref name="data" />.</summary>
    byte[] Wrap(string passphrase, byte[] data);

    /// <summary>Splits a wrapped blob into its embedded tag and the original data. Returns false — <paramref name="data" /> set to <paramref name="blob" /> unchanged — when no header is present (a blob predating this feature).</summary>
    bool TryUnwrap(byte[] blob, out byte[] tag, out byte[] data);

    /// <summary>True when <paramref name="tag" /> matches <paramref name="data" /> under the key derived from <paramref name="passphrase" /> (constant-time comparison).</summary>
    bool Verify(string passphrase, byte[] tag, byte[] data);
}
