using System.Security.Cryptography;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Outcome of a bundled-asset bootstrap attempt; the gate test turns errors into failures.</summary>
public sealed record BundledModelResult(bool AllPresent, IReadOnlyList<string> Errors);

/// <summary>
///     Locates and bootstraps the bundled int8 all-MiniLM-L6-v2 ONNX model + BERT vocab that
///     ship inside the tool package (FR-NM-3): pinned SHA-256, resolved from
///     AppContext.BaseDirectory/Models (or the repo source dir during tests) with an
///     AIRACCOON_EMBEDDING_MODEL env override for custom model paths.
/// </summary>
public static class BundledModel
{
    public const string ModelFileName = "model_qint8_arm64.onnx";
    public const string VocabFileName = "vocab.txt";

    // Pinned after the first verified download (2026-08-03) — the script and the gate test
    // share these so a tampered or drifted model fails loudly instead of degrading retrieval.
    public const string ModelSha256 = "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474";
    public const string VocabSha256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3";

    public const string ModelUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model_qint8_arm64.onnx";

    public const string VocabUrl =
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";

    /// <summary>
    ///     The ONNX model to embed with: the merged configured path (--embedding-model /
    ///     AIRACCOON_EMBEDDING_MODEL, null-or-whitespace = unset), else the bundled copy next
    ///     to the running tool, else the repo source copy during tests.
    /// </summary>
    public static string ResolveModelPath() => ResolveModelPath(null);

    public static string ResolveModelPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return ResolveBundled(ModelFileName)
            ?? throw new InvalidOperationException(
                $"Bundled embedding model '{ModelFileName}' not found next to the tool. Run " +
                "scripts/download-embedding-model.sh or set AIRACCOON_EMBEDDING_MODEL to a model path.");
    }

    public static string ResolveVocabPath() =>
        ResolveBundled(VocabFileName)
        ?? throw new InvalidOperationException(
            $"Bundled BERT vocab '{VocabFileName}' not found next to the tool. Run " +
            "scripts/download-embedding-model.sh.");

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    ///     Verifies both bundled files (sha256) and, when missing, downloads the pinned copies
    ///     into the repo's src/AiRaccoon/Models so the next build packs them. Never throws;
    ///     collects errors for the gate test — a missing asset is a hard failure, not a skip.
    /// </summary>
    public static async Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var model = LocateVerified(ModelFileName, ModelSha256);
        var vocab = LocateVerified(VocabFileName, VocabSha256);
        if (model is not null && vocab is not null)
        {
            return new BundledModelResult(true, errors);
        }

        var targetDir = RepoModelsDirectory() ?? Path.Combine(AppContext.BaseDirectory, "Models");
        Directory.CreateDirectory(targetDir);
        using var http = new HttpClient();

        if (model is null)
        {
            var error = await DownloadAsync(http, ModelUrl, Path.Combine(targetDir, ModelFileName), ModelSha256,
                cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (vocab is null)
        {
            var error = await DownloadAsync(http, VocabUrl, Path.Combine(targetDir, VocabFileName), VocabSha256,
                cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                errors.Add(error);
            }
        }

        return new BundledModelResult(errors.Count == 0, errors);
    }

    private static string? LocateVerified(string fileName, string expectedSha)
    {
        var path = ResolveBundled(fileName);
        return path is not null && Sha256Of(path).Equals(expectedSha, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static string? ResolveBundled(string fileName)
    {
        var relative = Path.Combine("Models", fileName);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
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

    private static async Task<string?> DownloadAsync(HttpClient http, string url, string target, string expectedSha,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return $"{Path.GetFileName(target)}: expected sha256 {expectedSha}, got {actual}";
            }

            await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return $"{Path.GetFileName(target)}: download failed ({ex.GetType().Name}: {ex.Message})";
        }
    }
}
