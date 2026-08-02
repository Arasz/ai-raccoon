namespace AiRaccon.Infrastructure.Provisioning;

/// <summary>Pinned SHA-256 checksums per release asset. Populate from the pinned releases before real provisioning; the provisioner refuses to download without one.</summary>
public static class ExtensionManifest
{
    public static string? Sha256(string assetFileName) => _hashes.GetValueOrDefault(assetFileName);

    private static readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
}
