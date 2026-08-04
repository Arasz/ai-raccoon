using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiRaccoon.Tests.Retrieval.Assets;

/// <summary>One pinned harness asset from the committed manifest.</summary>
public sealed record PinnedAsset(
    string Name,
    string Kind,
    string Url,
    string Sha256,
    string? LocalSource,
    string? Repo = null,
    string? Version = null,
    string? AssetFile = null)
{
    public bool IsArchive => AssetFile is not null;
}

/// <summary>Outcome of a bootstrap attempt; the gate test turns failures into hard test failures.</summary>
public sealed record EnsureResult(bool AllPresent, IReadOnlyList<string> Errors);

/// <summary>
///     Bootstraps the pinned reference assets into Retrieval/assets (gitignored): sqlite-memory
///     1.3.5 full + sqlite-vector 1.0.0 modules and the all-MiniLM GGUF model. Prefers verified
///     local copies (~/.ai-raccoon), falls back to the pinned GitHub/HuggingFace URLs, and always
///     verifies SHA-256 — a missing or mismatched asset is reported, never silently skipped.
/// </summary>
public sealed class ReferenceAssets
{
    public const string ManifestFileName = "manifest.json";
    public const string MemoryModuleName = "memory.dylib";
    public const string VectorModuleName = "vector.dylib";
    public const string ModelFileName = "all-MiniLM-L6-v2.Q5_K_M.gguf";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string AssetsDirectory { get; } = ResolveAssetsDirectory();

    public static string ManifestPath => Path.Combine(AssetsDirectory, ManifestFileName);

    public static string MemoryModulePath => Path.Combine(AssetsDirectory, MemoryModuleName);

    public static string VectorModulePath => Path.Combine(AssetsDirectory, VectorModuleName);

    public static string ModelPath => Path.Combine(AssetsDirectory, ModelFileName);

    public static IReadOnlyList<PinnedAsset> PinnedAssets { get; } = LoadManifest(ManifestPath);

    /// <summary>Copies or downloads every pinned asset; never throws — collects errors for the gate test.</summary>
    public static async Task<EnsureResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var errors = new List<string>();

        foreach (var asset in PinnedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = TargetPath(asset);
            var source = await EnsureAssetAsync(asset, target, http, cancellationToken).ConfigureAwait(false);
            if (source is not null && Sha256Of(target).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            errors.Add($"{asset.Name}: expected sha256 {asset.Sha256}, got " +
                       $"{(File.Exists(target) ? Sha256Of(target) : "<missing>")} (source: {source ?? "none"})");
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

    private static string TargetPath(PinnedAsset asset) =>
        asset.Name switch
        {
            MemoryModuleName => MemoryModulePath,
            VectorModuleName => VectorModulePath,
            ModelFileName => ModelPath,
            _ => Path.Combine(AssetsDirectory, asset.Name)
        };

    private static string? ExpandHome(string? path) => path is null ? null : Path.GetFullPath(path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

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

        var relative = Path.Combine("tests", "AiRaccoon.Tests", "Retrieval", "assets");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(Path.Combine(candidate, ManifestFileName)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not locate tests/AiRaccoon.Tests/Retrieval/assets from the test output directory; " +
            "set AIRACCOON_HARNESS_ASSETS to point at it.");
    }

    private sealed record ManifestFile(int SchemaVersion, string Platform, IReadOnlyList<PinnedAsset> Assets);
}
