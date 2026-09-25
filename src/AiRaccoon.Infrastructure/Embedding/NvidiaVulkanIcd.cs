namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Decisions for supplying NVIDIA's Vulkan ICD manifest to this process when a Linux container mounts
///     the driver (<c>libGLX_nvidia.so.0</c>) without the JSON that tells the Vulkan loader about it, which
///     otherwise leaves WebGPU with no adapter (ADR-0115 amendment).
/// </summary>
internal static class NvidiaVulkanIcd
{
    /// <summary>The NVIDIA Vulkan driver's library name.</summary>
    internal const string DriverLibrary = "libGLX_nvidia.so.0";

    /// <summary>Variables that already point the loader at drivers; any one set means leave it alone.</summary>
    internal static readonly string[] DriverVariables = ["VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_DRIVER_FILES"];

    /// <summary>Where distributions and NVIDIA's container toolkit put the driver library.</summary>
    internal static readonly string[] DriverDirectories =
    [
        "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu", "/usr/lib64", "/lib64", "/usr/lib",
        "/usr/lib/aarch64-linux-gnu", "/lib/aarch64-linux-gnu",
    ];

    /// <summary>The directories the Vulkan loader searches for ICD manifests on Linux, in its order.</summary>
    internal static IReadOnlyList<string> ManifestDirectories(string home, Func<string, string?> env)
    {
        static IEnumerable<string> Split(string? value, string fallback) =>
            (string.IsNullOrEmpty(value) ? fallback : value).Split(':', StringSplitOptions.RemoveEmptyEntries);

        var configHome = NonEmpty(env("XDG_CONFIG_HOME")) ?? $"{home}/.config";
        var dataHome = NonEmpty(env("XDG_DATA_HOME")) ?? $"{home}/.local/share";
        return
        [
            $"{configHome}/vulkan/icd.d",
            .. Split(env("XDG_CONFIG_DIRS"), "/etc/xdg").Select(d => $"{d}/vulkan/icd.d"),
            "/etc/vulkan/icd.d",
            $"{dataHome}/vulkan/icd.d",
            .. Split(env("XDG_DATA_DIRS"), "/usr/local/share:/usr/share").Select(d => $"{d}/vulkan/icd.d"),
        ];
    }

    /// <summary>True only when the driver is present and neither a manifest nor a driver variable reaches the loader.</summary>
    internal static bool NeedsManifest(bool anyManifestFound, string? driverPath, bool driverVariableSet) =>
        !anyManifestFound && driverPath is not null && !driverVariableSet;

    /// <summary>A Vulkan ICD manifest naming <paramref name="driverPath" />.</summary>
    internal static string Manifest(string driverPath) =>
        $$"""{ "file_format_version": "1.0.1", "ICD": { "library_path": "{{driverPath}}", "api_version": "1.4.312" } }""";

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
