namespace AiRaccoon.Infrastructure.Common;

/// <summary>Maps .NET RIDs to the asset platforms and module suffixes used by the sqliteai releases.</summary>
internal static class RuntimePlatform
{
    private static readonly Dictionary<string, string> SupportedAssetPlatforms = new()
    {
        ["osx-arm64"] = "macos-arm64",
        ["osx-x64"] = "macos-x86_64",
        ["linux-x64"] = "linux-x86_64",
        ["linux-arm64"] = "linux-arm64",
        ["win-x64"] = "windows-x86_64"
    };

    public static bool TryGetSupportedAssetPlatform(string rid, out string platform) => SupportedAssetPlatforms.TryGetValue(rid, out platform!);

    /// <summary>Asset platform for error reporting, including RIDs upstream has no binaries for.</summary>
    public static string ExpectedAssetPlatform(string rid) =>
        rid switch
        {
            "linux-musl-x64" => "linux-musl-x86_64",
            "linux-musl-arm64" => "linux-musl-arm64",
            "win-arm64" => "windows-arm64",
            _ => SupportedAssetPlatforms.TryGetValue(rid, out var platform)
                ? platform
                : throw new ArgumentOutOfRangeException(nameof(rid), rid, $"Unknown RID '{rid}'.")
        };

    public static string ModuleExtension(string rid) =>
        rid switch
        {
            "osx-arm64" or "osx-x64" => ".dylib",
            "linux-x64" or "linux-arm64" => ".so",
            "win-x64" => ".dll",
            _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, $"No module extension for RID '{rid}'.")
        };
}
