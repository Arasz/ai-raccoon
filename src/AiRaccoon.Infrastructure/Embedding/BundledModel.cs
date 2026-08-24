using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Assets;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Outcome of a bundled-asset bootstrap attempt; the gate test turns errors into failures.</summary>
public sealed record BundledModelResult(bool AllPresent, IReadOnlyList<string> Errors);

/// <summary>
///     Locates and bootstraps the bundled int8 all-MiniLM-L6-v2 ONNX model + BERT vocab that ship
///     inside the tool package (FR-NM-3; see docs/work/features-native-memory/native-memory.feature),
///     pinned by SHA-256. A custom model path comes from the embedding.model settings row.
/// </summary>
public sealed partial class BundledModel(ILogger<BundledModel> logger, IHttpClientFactory httpClientFactory) : IBundledModel
{
    public const string ModelFileName = "model_qint8_arm64.onnx";
    private const string VocabFileName = "vocab.txt";

    // Pinned after the first verified download — the script and the gate test share these so a
    // tampered or drifted model fails loudly instead of degrading retrieval.
    public const string ModelSha256 = "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474";
    public const string VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    private const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx";

    private const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";


    /// <summary>
    ///     Verifies both bundled files (sha256) and, when missing, downloads the pinned copies
    ///     into the repo's src/AiRaccoon/Models so the next build packs them. Download failures
    ///     become error entries — a missing asset is a hard failure for the gate test, not a skip.
    /// </summary>
    public async Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var model = LocateVerified(ModelFileName, ModelSha256);
        var vocab = LocateVerified(VocabFileName, VocabSha256);
        if (model is not null && vocab is not null)
        {
            Log.BundledModelAssetsVerified(logger);
            return new BundledModelResult(true, []);
        }

        var targetDir = RepoModelsDirectory() ?? Path.Combine(AppContext.BaseDirectory, "Models");
        return await EnsureDownloadsAsync(targetDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Downloads both bundled assets into targetDirectory when no verified copy sits there; download failures become
    ///     error entries (see docs/work/features-native-memory/native-memory.feature).
    /// </summary>
    public async Task<BundledModelResult> EnsureDownloadsAsync(string targetDirectory, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(targetDirectory);
        var httpClient = httpClientFactory.CreateClient();
        errors.AddRange(await DownloadResources(httpClient, new BundledResource(targetDirectory, ModelFileName, ModelUrl, ModelSha256), cancellationToken));
        errors.AddRange(await DownloadResources(httpClient, new BundledResource(targetDirectory, VocabFileName, VocabUrl, VocabSha256), cancellationToken));
        return new BundledModelResult(errors.Count == 0, errors);
    }

    /// <summary>
    ///     The ONNX model to embed with: the settings row embedding.model (written by
    ///     'ai-raccoon model set local &lt;path&gt;', null-or-whitespace = unset), else the bundled copy next
    ///     to the running tool, else the repo source copy during tests.
    /// </summary>
    public static string ResolveModelPath() => ResolveModelPath(null, AppContext.BaseDirectory);

    internal static string ResolveModelPath(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return ResolveBundled(ModelFileName, baseDirectory)
               ?? throw BundledAssetUnavailable("embedding model", ModelFileName, baseDirectory, MissingBundledModelMessage(ModelFileName));
    }

    /// <summary>
    ///     Actionable message for a missing bundled asset; points at 'ai-raccoon model set local' (see
    ///     docs/work/features-native-memory/native-memory.feature).
    /// </summary>
    internal static string MissingBundledModelMessage(string fileName) =>
        $"Bundled embedding model '{fileName}' not found next to the tool. Run 'ai-raccoon model set local' to restore it, or 'ai-raccoon model set local <path-to-onnx>' for a custom path.";

    public static string ResolveVocabPath() => ResolveVocabPath(AppContext.BaseDirectory);

    internal static string ResolveVocabPath(string baseDirectory) =>
        ResolveBundled(VocabFileName, baseDirectory)
        ?? throw BundledAssetUnavailable("BERT vocab", VocabFileName, baseDirectory, MissingBundledVocabMessage(VocabFileName));

    /// <summary>Actionable message for a missing bundled vocab; points at 'ai-raccoon model set local'.</summary>
    internal static string MissingBundledVocabMessage(string fileName) => $"Bundled BERT vocab '{fileName}' not found next to the tool. Run 'ai-raccoon model set local' to restore it.";

    /// <summary>
    ///     Distinguishes a genuinely-missing bundled asset (baseDirectory exists, the file does not —
    ///     'model set local' fixes it) from an install replaced out from under this process
    ///     (baseDirectory itself no longer exists, e.g. after 'dotnet tool update' — only a restart
    ///     fixes it).
    /// </summary>
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


    private static string? LocateVerified(string fileName, string expectedSha)
    {
        var path = ResolveBundled(fileName);
        return path is not null && BundledResource.Sha256Of(path).Equals(expectedSha, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
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
