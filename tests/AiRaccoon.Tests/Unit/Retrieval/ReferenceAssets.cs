using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>One pinned harness asset from the committed manifest.</summary>
public sealed record PinnedAsset(
    string Name,
    string Kind,
    string Url,
    string Sha256,
    string? Platform,
    string? LocalSource,
    string? Repo = null,
    string? Version = null,
    string? AssetFile = null)
{
    public bool IsArchive => AssetFile is not null;
}

/// <summary>Outcome of a bootstrap attempt; the gate test turns errors into hard test failures.</summary>
public sealed record EnsureResult(bool AllPresent, IReadOnlyList<string> Errors);

/// <summary>
///     Bootstraps the pinned reference assets into Retrieval/assets (gitignored): sqlite-memory
///     1.3.5 + sqlite-vector 1.0.0 for the current platform, plus the all-MiniLM GGUF model.
///     Prefers verified local copies, falls back to pinned URLs, and always verifies SHA-256.
/// </summary>
public sealed class ReferenceAssets
{
    public const string ManifestFileName = "manifest.json";
    public const string ModelFileName = "all-MiniLM-L6-v2.Q5_K_M.gguf";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string AssetsDirectory { get; } = ResolveAssetsDirectory();

    public static string ManifestPath => Path.Combine(AssetsDirectory, ManifestFileName);

    /// <summary>The platform the current process runs on: "osx-arm64" or "linux-x64".</summary>
    public static string CurrentPlatform { get; } = ResolveCurrentPlatform();

    public static string MemoryModuleName => MemoryModuleAsset.Name;
    public static string VectorModuleName => VectorModuleAsset.Name;

    public static string MemoryModulePath => Path.Combine(AssetsDirectory, MemoryModuleName);
    public static string VectorModulePath => Path.Combine(AssetsDirectory, VectorModuleName);
    public static string ModelPath => Path.Combine(AssetsDirectory, ModelFileName);

    /// <summary>Every asset pinned by the manifest (all platforms — used by the policy tests).</summary>
    public static IReadOnlyList<PinnedAsset> PinnedAssets { get; } = LoadManifest(ManifestPath);

    /// <summary>The assets that apply to the current platform: modules for CurrentPlatform + the model.</summary>
    public static IReadOnlyList<PinnedAsset> ActiveAssets => [.. PinnedAssets.Where(a => a.Platform is null || a.Platform == CurrentPlatform)];

    private static PinnedAsset MemoryModuleAsset => ActiveAssets.Single(a => a.Repo == "sqlite-memory");

    private static PinnedAsset VectorModuleAsset => ActiveAssets.Single(a => a.Repo == "sqlite-vector");

    /// <summary>Copies or downloads every active pinned asset; never throws — collects errors for the gate test.</summary>
    public static async Task<EnsureResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var errors = new List<string>();

        foreach (var asset in ActiveAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = TargetPath(asset);
            var source = await EnsureAssetAsync(asset, target, http, cancellationToken).ConfigureAwait(false);
            if (source is not null && Sha256Of(target).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            errors.Add($"{asset.Name}: expected sha256 {asset.Sha256}, got {(File.Exists(target) ? Sha256Of(target) : "<missing>")} (source: {source ?? "none"})");
        }

        return new EnsureResult(errors.Count == 0, errors);
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<string?> EnsureAssetAsync(
        PinnedAsset asset, string target, HttpClient http, CancellationToken cancellationToken)
    {
        if (File.Exists(target) && Sha256Of(target).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "present";
        }

        var local = ExpandHome(asset.LocalSource);
        if (local is not null && File.Exists(local) &&
            Sha256Of(local).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(local, target, true);
            return $"copy:{local}";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = await http.GetByteArrayAsync(asset.Url, cancellationToken).ConfigureAwait(false);
            if (asset.IsArchive)
            {
                ExtractModule(bytes, asset, target);
            }
            else
            {
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            }

            return $"download:{asset.Url}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return $"failed({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static void ExtractModule(byte[] archive, PinnedAsset asset, string target)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new MemoryStream();
        gzip.CopyTo(tar);
        tar.Position = 0;

        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            TarFile.ExtractToDirectory(tar, staging, true);
            var modulePrefix = Path.GetFileNameWithoutExtension(asset.Name);
            var extracted = Directory.EnumerateFiles(staging)
                .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file).StartsWith(modulePrefix, StringComparison.Ordinal));
            if (extracted is null)
            {
                throw new InvalidDataException($"Archive '{asset.AssetFile}' contained no '{modulePrefix}' module.");
            }

            File.Move(extracted, target, true);
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    private static string TargetPath(PinnedAsset asset) => Path.Combine(AssetsDirectory, asset.Name);

    private static string? ExpandHome(string? path) => path is null ? null : Path.GetFullPath(path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    private static string ResolveCurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture;
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : throw new PlatformNotSupportedException(
                $"The golden-retrieval harness supports macOS and Linux; running on {RuntimeInformation.OSDescription}.");

        var ridArch = arch switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => throw new PlatformNotSupportedException(
                $"The golden-retrieval harness supports arm64 and x64; running on {arch}.")
        };

        return $"{os}-{ridArch}";
    }

    private static IReadOnlyList<PinnedAsset> LoadManifest(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(manifestPath), JsonOptions);
        return manifest?.Assets ?? throw new InvalidDataException($"manifest {manifestPath} has no assets.");
    }

    private static string ResolveAssetsDirectory()
    {
        if (Environment.GetEnvironmentVariable("AIRACCOON_HARNESS_ASSETS") is { Length: > 0 } env)
        {
            return Path.GetFullPath(env);
        }

        // Try source-tree path first (walking up from output)
        var relative = Path.Combine("tests", "AiRaccoon.Tests", "Unit", "Retrieval", "assets");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(Path.Combine(candidate, ManifestFileName)))
            {
                return candidate;
            }
        }

        // Fallback: CopyToOutputDirectory places assets at <output>/Unit/Retrieval/assets/
        var outputRelative = Path.Combine(AppContext.BaseDirectory, "Unit", "Retrieval", "assets");
        if (File.Exists(Path.Combine(outputRelative, ManifestFileName)))
        {
            return outputRelative;
        }

        throw new InvalidOperationException(
            "Could not locate Unit/Retrieval/assets/ from the test output directory; " +
            "set AIRACCOON_HARNESS_ASSETS to point at it.");
    }

    private sealed record ManifestFile(int SchemaVersion, IReadOnlyList<PinnedAsset> Assets);
}
