namespace AiRaccoon.Infrastructure.Provisioning;

/// <summary>Pinned SHA-256 checksums per release asset, verified from the pinned GitHub releases (2026-08-03).</summary>
public static class ExtensionManifest
{
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.Ordinal)
    {
        // sqlite-sync 1.1.2 (cloudsync)
        ["cloudsync-macos-arm64-1.1.2.tar.gz"] = "e87d8e0ea5681b2a7486bca1cc404155db009e81100b309420d160fcde9e01ac",
        ["cloudsync-macos-x86_64-1.1.2.tar.gz"] = "bb8854042ddd2e6d237dc5fd6f76a0cd72082afa9143076bb2e53ff28e91b252",
        ["cloudsync-linux-x86_64-1.1.2.tar.gz"] = "1aaad0a0a891a5fdae2bb95ca042a535165fd44aca2bf67cc2827285e5ed3c99",
        ["cloudsync-linux-arm64-1.1.2.tar.gz"] = "e106da0ffdbc27319af05a7708a7dd11b795b377826e40146f6a53478f1b77ac",
        ["cloudsync-windows-x86_64-1.1.2.tar.gz"] = "6916f97b19256c5eca0254c77552ca4420b2535a6c2fad0ea4b4447da9e3a130"
    };

    public static string? Sha256(string assetFileName) => Hashes.GetValueOrDefault(assetFileName);
}
