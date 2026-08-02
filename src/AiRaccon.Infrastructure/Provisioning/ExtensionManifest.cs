namespace AiRaccon.Infrastructure.Provisioning;

/// <summary>Pinned SHA-256 checksums per release asset, verified from the pinned GitHub releases (2026-08-03).</summary>
public static class ExtensionManifest
{
    public static string? Sha256(string assetFileName) => _hashes.GetValueOrDefault(assetFileName);

    private static readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal)
    {
        // sqlite-vector 1.0.0
        ["vector-macos-arm64-1.0.0.tar.gz"] = "4974e059304cc5b83b2d6e866411a2123ac0c78b43e08c27d45c836b36198efa",
        ["vector-macos-x86_64-1.0.0.tar.gz"] = "707c97dc58a9e3e41767a6814baac7674054f5f2d2af3494321ad8beb2537ec8",
        ["vector-linux-x86_64-1.0.0.tar.gz"] = "0d38ccafd603767cef0709b7f98b6d570c02095dc9767418c098cc5e903965fb",
        ["vector-linux-arm64-1.0.0.tar.gz"] = "8da8e8dc972d20318712b3326551782d1f755c4f22d3cf55ede474001569724a",
        ["vector-windows-x86_64-1.0.0.tar.gz"] = "f5be566b1e9b4608cbfd4a575d18d8018a50c32bc45e53436c5f9b74fac7a633",

        // sqlite-memory 1.3.5 (full flavor: local + remote engines)
        ["memory-macos-arm64-full-1.3.5.tar.gz"] = "2f0168afe19093048b7c26a819f292c247f8ad3fa4f501e783f09fa0419dc057",
        ["memory-macos-x86_64-full-1.3.5.tar.gz"] = "5e3789e2f76dfa78fcb1c27b810b14d5da8a5f4de61a3fa2f22ab6f2870742c3",
        ["memory-linux-x86_64-full-1.3.5.tar.gz"] = "7399e30de1109a4393617f351087ec938eb754e95e7919492d9c8177d583cd9a",
        ["memory-linux-arm64-full-1.3.5.tar.gz"] = "666a12be6e226d40fa6f05af8dbc568ebb25b91d622ccafbfed19fc71c037ae3",
        ["memory-windows-x86_64-full-1.3.5.tar.gz"] = "a90a494cdbb5cbf772eca2e59f79ea16fd2fc2bb2ba61219b6eb7669f91de7e3",

        // sqlite-sync 1.1.2 (cloudsync)
        ["cloudsync-macos-arm64-1.1.2.tar.gz"] = "e87d8e0ea5681b2a7486bca1cc404155db009e81100b309420d160fcde9e01ac",
        ["cloudsync-macos-x86_64-1.1.2.tar.gz"] = "bb8854042ddd2e6d237dc5fd6f76a0cd72082afa9143076bb2e53ff28e91b252",
        ["cloudsync-linux-x86_64-1.1.2.tar.gz"] = "1aaad0a0a891a5fdae2bb95ca042a535165fd44aca2bf67cc2827285e5ed3c99",
        ["cloudsync-linux-arm64-1.1.2.tar.gz"] = "e106da0ffdbc27319af05a7708a7dd11b795b377826e40146f6a53478f1b77ac",
        ["cloudsync-windows-x86_64-1.1.2.tar.gz"] = "6916f97b19256c5eca0254c77552ca4420b2535a6c2fad0ea4b4447da9e3a130",
    };
}
