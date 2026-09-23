using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Outcome of a bundled-asset bootstrap attempt; the gate test turns errors into failures.</summary>
public sealed record BundledModelResult(bool AllPresent, IReadOnlyList<string> Errors);

/// <summary>
///     Locates and bootstraps the bundled engine (ADR-0108): granite-embedding-small-english-r2
///     fp16, shipped as a manifest model directory under <c>Models/</c> whose committed
///     <c>ai-raccoon.manifest.json</c> pins every file by SHA-256. Also ships the legacy BERT vocab
///     that the single-file <c>.onnx</c> path tokenizes with.
/// </summary>
public sealed partial class BundledModel(ILogger<BundledModel> logger, IHttpClientFactory httpClientFactory) : IBundledModel
{
    /// <summary>The bundled engine's directory under <c>Models/</c>.</summary>
    public const string DirectoryName = "granite-embedding-small-english-r2";

    /// <summary>The settings value that names the bundled engine where a model path is expected.</summary>
    public const string SettingValue = "bundled";

    private const string VocabFileName = "vocab.txt";
    public const string VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    /// <summary>True when <paramref name="model" /> names the bundled engine: unset or <see cref="SettingValue" />.</summary>
    public static bool IsBundled(string? model) =>
        string.IsNullOrWhiteSpace(model) || string.Equals(model, SettingValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Verifies every file the bundled manifest pins plus the legacy vocab and, when one is
    ///     missing, downloads the pinned copy into the repo's src/AiRaccoon/Models so the next build
    ///     packs it. Download failures become error entries — a missing asset fails the gate test.
    /// </summary>
    public async Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var directory = ResolveDirectoryOrNull(AppContext.BaseDirectory);
        if (directory is not null && ManifestFiles(directory).All(f => f.IsVerified()) && ResolveBundled(VocabFileName) is { } vocab
            && BundledResource.Sha256Of(vocab).Equals(VocabSha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.BundledModelAssetsVerified(logger);
            return new BundledModelResult(true, []);
        }

        var targetDir = RepoModelsDirectory() ?? Path.Combine(AppContext.BaseDirectory, "Models");
        return await EnsureDownloadsAsync(targetDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Downloads each file the manifest in <c>targetDirectory/DirectoryName</c> pins, plus the
    ///     legacy vocab, when no verified copy sits there. The manifest itself is committed, never fetched.
    /// </summary>
    public async Task<BundledModelResult> EnsureDownloadsAsync(string targetDirectory, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(targetDirectory);
        var httpClient = httpClientFactory.CreateClient();
        errors.AddRange(await DownloadResources(httpClient, new BundledResource(targetDirectory, VocabFileName, VocabUrl, VocabSha256), cancellationToken));
        var bundled = Path.Combine(targetDirectory, DirectoryName);
        if (!File.Exists(Path.Combine(bundled, EmbeddingManifest.FileName)))
        {
            errors.Add($"{DirectoryName}/{EmbeddingManifest.FileName}: missing — it is committed with the source, never downloaded");
            return new BundledModelResult(false, errors);
        }

        foreach (var file in ManifestFiles(bundled))
        {
            errors.AddRange(await DownloadResources(httpClient, file, cancellationToken));
        }

        return new BundledModelResult(errors.Count == 0, errors);
    }

    /// <summary>The bundled engine's directory next to the tool (or in the source tree during development).</summary>
    public static string ResolveDirectory() => ResolveDirectory(AppContext.BaseDirectory);

    internal static string ResolveDirectory(string baseDirectory) =>
        ResolveDirectoryOrNull(baseDirectory)
        ?? throw BundledAssetUnavailable("embedding model", DirectoryName, baseDirectory,
            $"Bundled embedding model directory '{DirectoryName}' not found next to the tool. Reinstall the tool to restore it, " +
            "or 'ai-raccoon model embedding set local <model-dir>' for a downloaded model.");

    private static string? ResolveDirectoryOrNull(string baseDirectory)
    {
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Models", DirectoryName);
            if (File.Exists(Path.Combine(candidate, EmbeddingManifest.FileName)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every file the bundled manifest pins, with the upstream URL at the pinned revision.</summary>
    private static IEnumerable<BundledResource> ManifestFiles(string directory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, EmbeddingManifest.FileName)));
        var root = doc.RootElement;
        var source = root.GetProperty("source");
        var baseUrl = $"https://huggingface.co/{source.GetProperty("repo").GetString()}/resolve/{source.GetProperty("revision").GetString()}/";
        var files = new List<BundledResource>();
        void Add(JsonElement list, string upstreamDir)
        {
            foreach (var file in list.EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!;
                files.Add(new BundledResource(directory, path, baseUrl + upstreamDir + path, file.GetProperty("sha256").GetString()!));
            }
        }

        Add(root.GetProperty("onnx").GetProperty("files"), "onnx/");
        Add(root.GetProperty("tokenizer").GetProperty("files"), "");
        if (root.TryGetProperty("provenanceFiles", out var provenance) && provenance.ValueKind == JsonValueKind.Array)
        {
            Add(provenance, "");
        }

        return files;
    }

    public static string ResolveVocabPath() => ResolveVocabPath(AppContext.BaseDirectory);

    internal static string ResolveVocabPath(string baseDirectory) =>
        ResolveBundled(VocabFileName, baseDirectory)
        ?? throw BundledAssetUnavailable("BERT vocab", VocabFileName, baseDirectory, MissingBundledVocabMessage(VocabFileName));

    internal static string MissingBundledVocabMessage(string fileName) =>
        $"Bundled BERT vocab '{fileName}' not found next to the tool. Run 'ai-raccoon model embedding set local' to restore it.";

    private static InvalidOperationException BundledAssetUnavailable(string assetLabel, string fileName, string baseDirectory, string missingAssetMessage) =>
        Directory.Exists(baseDirectory)
            ? new InvalidOperationException(missingAssetMessage)
            : new BundledModelInstallReplacedException(assetLabel, fileName, baseDirectory);

    private async Task<List<string>> DownloadResources(HttpClient httpClient, BundledResource resource, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        if (resource.IsVerified())
        {
            return errors;
        }

        Log.DownloadingBundledModelAsset(logger, resource.Name, resource.ResourcePath);
        var error = await DownloadAsync(httpClient, resource.Url, resource.ResourcePath, resource.Sha256, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            Log.FailedToDownloadBundledModelAsset(logger, resource.Name, error);
            errors.Add(error);
        }

        return errors;
    }


    private static string? ResolveBundled(string fileName) => ResolveBundled(fileName, AppContext.BaseDirectory);

    internal static string? ResolveBundled(string fileName, string baseDirectory)
    {
        var relative = Path.Combine("Models", fileName);
        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var flatCandidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(flatCandidate))
            {
                return flatCandidate;
            }
        }

        return null;
    }

    private static string? RepoModelsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AiRaccoon", "Models");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<string?> DownloadAsync(HttpClient http, string url, string target, string expectedSha,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await new AssetDownloader(http).GetAsync(url, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return $"{Path.GetFileName(target)}: expected sha256 {expectedSha}, got {actual}";
            }

            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (EmptyDownloadException ex)
        {
            Log.EmptyBundledModelDownload(logger, Path.GetFileName(target), url);
            return $"{Path.GetFileName(target)}: {ex.Message}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return $"{Path.GetFileName(target)}: download failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    public static partial class Log
    {
        [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "Downloading bundled model asset {Name} to {Path}")]
        public static partial void DownloadingBundledModelAsset(ILogger logger, string name, string path);

        [LoggerMessage(EventId = 411, Level = LogLevel.Error, Message = "Failed to download bundled model asset {Name}: {Error}")]
        public static partial void FailedToDownloadBundledModelAsset(ILogger logger, string name, string error);

        [LoggerMessage(EventId = 412, Level = LogLevel.Debug, Message = "Bundled model assets verified")]
        public static partial void BundledModelAssetsVerified(ILogger logger);

        [LoggerMessage(EventId = 413, Level = LogLevel.Error, Message = "Bundled model asset {Name} downloaded 0 bytes from {Url}")]
        public static partial void EmptyBundledModelDownload(ILogger logger, string name, string url);
    }
}

public sealed record BundledResource(string Directory, string Name, string Url, string Sha256)
{
    public string ResourcePath => Path.Combine(Directory, Name);

    public bool IsVerified() => File.Exists(ResourcePath) && Sha256Of(ResourcePath).Equals(Sha256, StringComparison.OrdinalIgnoreCase);

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
