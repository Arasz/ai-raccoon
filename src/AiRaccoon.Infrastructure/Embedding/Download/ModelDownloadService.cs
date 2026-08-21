using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Resilience;

namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>Inputs to a model download run (the CLI verb maps its flags onto this record).</summary>
public sealed record ModelDownloadRequest(
    string RepoId,
    string Revision,
    string TargetDirectory,
    IReadOnlyList<string>? ExplicitFiles = null,
    bool DryRun = false,
    bool Yes = false,
    Func<string, bool>? Confirm = null);

/// <summary>Outcome of a download run; dry runs carry the plan and nothing on disk.</summary>
public sealed record ModelDownloadResult(
    ModelDownloadPlan Plan,
    string TargetDirectory,
    string? ManifestPath,
    IReadOnlyList<string> DownloadedFiles);

/// <summary>A download failed after the plan was accepted: SHA mismatch, fetch failure, ORT smoke
/// failure. The failed run leaves no half-installed model behind.</summary>
public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The run was refused before any download: size confirmation declined or insufficient
/// disk space. Nothing was touched.</summary>
public sealed class ModelDownloadRejectedException(string message) : Exception(message);

/// <summary>Loads a downloaded ONNX model in ONNX Runtime as an opset compatibility smoke test (D4 m10).</summary>
public interface IOnnxSmokeTester
{
    /// <summary>Throws <see cref="OnnxSmokeTestException" /> when the model cannot be loaded.</summary>
    void Verify(string onnxPath);
}

public sealed class OnnxSmokeTestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Real smoke tester: an InferenceSession constructor over the downloaded graph.</summary>
public sealed class OrtOnnxSmokeTester : IOnnxSmokeTester
{
    public void Verify(string onnxPath)
    {
        try
        {
            using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(onnxPath);
        }
        catch (Exception ex)
        {
            throw new OnnxSmokeTestException(
                $"the downloaded model failed to load in ONNX Runtime: {ex.Message} — the graph or its external data may be corrupt, or the export uses an unsupported opset", ex);
        }
    }
}

/// <summary>Free bytes on the volume hosting a path (injected so tests can fake full disks).</summary>
public interface IDiskSpaceProvider
{
    long AvailableFreeBytes(string path);
}

public sealed class DiskSpaceProvider : IDiskSpaceProvider
{
    public long AvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        return new DriveInfo(root).AvailableFreeSpace;
    }
}

/// <summary>The <c>&lt;data-root&gt;/models/&lt;slug&gt;</c> slug: repo id sanitized for the file
/// system (plan §5.4, e.g. <c>BAAI/bge-m3</c> → <c>BAAI__bge-m3</c>).</summary>
public static class ModelSlug
{
    public static string Sanitize(string repoId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(repoId.Length);
        foreach (var ch in repoId)
        {
            // '/' becomes "__" — the plan's slug convention (BAAI/bge-m3 → BAAI__bge-m3).
            builder.Append(ch == '/'
                ? "__"
                : invalid.Contains(ch) || char.IsWhiteSpace(ch) || ch < 32 ? "_" : ch.ToString());
        }

        return builder.ToString();
    }
}

/// <summary>
///     Orchestrates a verified model download (plan §8, D4/D8): resolve the tree, fetch the
///     config provenance, plan (glob external data for the pre-download size guard), guard the
///     size (&gt;500 MB needs --yes or an interactive confirm) and disk space, download the ONNX
///     stub, re-plan from the protobuf (external-data enumeration), download everything
///     verify-or-delete via the <c>BundledResource.IsVerified</c> pattern, run the ORT opset
///     smoke test, and only then write manifest.json. Any failure removes this run's files and
///     .part residue — a pre-existing verified model in the same directory is left intact.
/// </summary>
public sealed class ModelDownloadService(
    HfTreeClient treeClient,
    AssetDownloader downloader,
    IModelDownloadPlanner planner,
    IOnnxGraphProbeReader probeReader,
    IOnnxSmokeTester smokeTester,
    IDiskSpaceProvider diskSpace,
    IEmbeddingManifestSerializer manifestSerializer,
    IEmbeddingManifestValidator manifestValidator)
{
    /// <summary>Downloads above this total size require --yes (plan §8.3 multi-GB guard).</summary>
    public const long LargeDownloadThresholdBytes = 500L * 1024 * 1024;

    public async Task<ModelDownloadResult> DownloadAsync(ModelDownloadRequest request, CancellationToken cancellationToken)
    {
        var tree = await treeClient.GetTreeAsync(request.RepoId, request.Revision, cancellationToken).ConfigureAwait(false);
        var rawFiles = await FetchProvenanceAsync(request, tree, cancellationToken).ConfigureAwait(false);

        // Phase 1: glob-based plan (external data by filename pattern — nothing downloaded yet).
        var preliminary = planner.BuildPlan(request.RepoId, request.Revision, tree, rawFiles, probe: null, request.ExplicitFiles);
        if (request.DryRun)
        {
            return new ModelDownloadResult(preliminary, request.TargetDirectory, null, []);
        }

        GuardSize(preliminary, request);
        GuardDiskSpace(preliminary, request.TargetDirectory);

        var targetDir = request.TargetDirectory;
        var downloaded = new List<string>();
        var createdTargetDir = !Directory.Exists(targetDir);
        var cleanup = new List<string>();
        try
        {
            Directory.CreateDirectory(targetDir);

            // The ONNX stub must land before the protobuf probe can name its external data.
            var stub = preliminary.Files.Single(f => f.Path == preliminary.ModelFilePath);
            await DownloadVerifiedAsync(request, stub, preliminary.ModelFilePath, targetDir, downloaded, cleanup, cancellationToken).ConfigureAwait(false);

            var probe = probeReader.Read(File.ReadAllBytes(Path.Combine(targetDir, TargetPath(stub.Path, preliminary.ModelFilePath))));
            var plan = planner.BuildPlan(request.RepoId, request.Revision, tree, rawFiles, probe, request.ExplicitFiles);

            if (plan.TotalSize() > preliminary.TotalSize())
            {
                // The graph declared external data the glob missed — honest re-check before the big files.
                GuardDiskSpace(plan, targetDir);
            }

            foreach (var file in plan.Files.Where(f => f.Path != stub.Path))
            {
                await DownloadVerifiedAsync(request, file, plan.ModelFilePath, targetDir, downloaded, cleanup, cancellationToken).ConfigureAwait(false);
            }

            // ORT opset smoke test at download-verify time (D4 m10) — before the manifest exists,
            // so a model that cannot load is never recorded as installed.
            try
            {
                smokeTester.Verify(Path.Combine(targetDir, TargetPath(plan.ModelFilePath, plan.ModelFilePath)));
            }
            catch (OnnxSmokeTestException ex)
            {
                throw new ModelDownloadException(ex.Message, ex);
            }

            var manifestPath = await WriteManifestAsync(plan, targetDir, cancellationToken).ConfigureAwait(false);
            return new ModelDownloadResult(plan, targetDir, manifestPath, downloaded);
        }
        catch (Exception)
        {
            Cleanup(targetDir, cleanup, createdTargetDir);
            throw;
        }
    }

    /// <summary>Fetches config.json + tokenizer_config.json (the dims/ctx and special-token-id
    /// sources) and, when present, 1_Pooling/config.json + modules.json (D11 pooling provenance).
    /// The config candidates are resolved exactly like the planner resolves them (beside the
    /// model file first, then the repo root) so a repo's own 1_Pooling/config.json is never
    /// mistaken for the model config.</summary>
    private async Task<Dictionary<string, string>> FetchProvenanceAsync(
        ModelDownloadRequest request, IReadOnlyList<HfTreeEntry> tree, CancellationToken cancellationToken)
    {
        var modelDir = ModelDirectoryOf(planner.SelectModelFilePath(tree, request.ExplicitFiles));
        var rawFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in new[] { "config.json", "tokenizer_config.json" })
        {
            var candidate = TreeEntryAt(tree, modelDir, name);
            if (candidate is not null)
            {
                rawFiles[candidate] = await FetchRawAsync(request, candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var name in new[] { "1_Pooling/config.json", "modules.json" })
        {
            if (tree.Any(e => e.Path == name))
            {
                rawFiles[name] = await FetchRawAsync(request, name, cancellationToken).ConfigureAwait(false);
            }
        }

        return rawFiles;
    }

    private static string ModelDirectoryOf(string modelFilePath) =>
        modelFilePath.Contains('/') ? modelFilePath[..modelFilePath.LastIndexOf('/')] : string.Empty;

    private static string? TreeEntryAt(IReadOnlyList<HfTreeEntry> tree, string modelDir, string name)
    {
        var besideModel = modelDir.Length == 0 ? name : $"{modelDir}/{name}";
        return tree.Any(e => e.Path == besideModel) ? besideModel
            : tree.Any(e => e.Path == name) ? name
            : null;
    }

    private async Task<string> FetchRawAsync(ModelDownloadRequest request, string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await downloader.GetAsync(ResolveUrl(request, path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or EmptyDownloadException)
        {
            throw new ModelDownloadException($"failed to fetch '{path}' from '{request.RepoId}' at '{request.Revision}': {ex.Message}", ex);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Verify-or-delete (BundledResource.IsVerified pattern): skip when the target is
    /// already verified; otherwise .part → SHA-256 check → atomic rename, delete on mismatch.</summary>
    private async Task DownloadVerifiedAsync(
        ModelDownloadRequest request,
        PinnedFile file,
        string modelFilePath,
        string targetDir,
        List<string> downloaded,
        List<string> cleanup,
        CancellationToken cancellationToken)
    {
        var targetRel = TargetPath(file.Path, modelFilePath);
        var targetPath = Path.Combine(targetDir, targetRel);
        var expected = file.LfsSha256;
        if (expected is not null && File.Exists(targetPath) && Sha256Of(targetPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var partPath = targetPath + ".part";
        try
        {
            await DownloadToFileAsync(ResolveUrl(request, file.Path), partPath, cancellationToken).ConfigureAwait(false);
            var actual = Sha256Of(partPath);
            if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelDownloadException(
                    $"sha256 mismatch for '{file.Path}': expected {expected}, got {actual}. " +
                    "The file was deleted — re-run the download; if it keeps failing, the repo changed (re-resolve) or the channel is untrusted.");
            }

            File.Move(partPath, targetPath);
            cleanup.Add(targetPath);
            downloaded.Add(targetRel);
        }
        catch (Exception ex) when (ex is not ModelDownloadException)
        {
            throw new ModelDownloadException($"failed to download '{file.Path}' from '{request.RepoId}' at '{request.Revision}': {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(partPath))
            {
                try
                {
                    File.Delete(partPath);
                }
                catch (IOException)
                {
                    // best-effort; the run still fails on the original exception
                }
            }
        }
    }

    /// <summary>Streams a URL to a file (multi-GB safe — never buffers the body in memory) with
    /// the same Polly retry pipeline the asset downloader uses, and fails on zero-length bodies.</summary>
    private async Task DownloadToFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        var pipeline = ResiliencePipelineFactory.CreateAssetDownloaderPipeline();
        await pipeline.ExecuteAsync(async ct =>
        {
            using var response = await treeClient.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{url} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            if (file.Length == 0)
            {
                throw new EmptyDownloadException($"{url} returned 0 bytes; nothing was downloaded to verify");
            }

            return response;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void GuardSize(ModelDownloadPlan plan, ModelDownloadRequest request)
    {
        var total = plan.TotalSize();
        if (total <= LargeDownloadThresholdBytes)
        {
            return;
        }

        var message =
            $"the download totals {total / (1024.0 * 1024.0):F1} MiB, which exceeds the {LargeDownloadThresholdBytes / (1024.0 * 1024.0):F0} MiB guard. " +
            "Pass --yes to confirm, or re-run and answer the prompt.";
        if (request.Yes || request.Confirm?.Invoke(message) == true)
        {
            return;
        }

        throw new ModelDownloadRejectedException(message);
    }

    private void GuardDiskSpace(ModelDownloadPlan plan, string targetDirectory)
    {
        var free = diskSpace.AvailableFreeBytes(targetDirectory);
        if (free >= plan.TotalSize())
        {
            return;
        }

        throw new ModelDownloadRejectedException(
            $"insufficient disk space: the download needs {plan.TotalSize() / (1024.0 * 1024.0):F1} MiB but only {free / (1024.0 * 1024.0):F1} MiB are free on the volume hosting '{targetDirectory}'");
    }

    private async Task<string> WriteManifestAsync(ModelDownloadPlan plan, string targetDir, CancellationToken cancellationToken)
    {
        var manifest = new EmbeddingManifest(
            ManifestVersion: 1,
            Model: plan.RepoId,
            Source: new ManifestSource(plan.RepoId, plan.Revision),
            Provider: ManifestProvider.Local,
            Dimensions: plan.Dimensions,
            ContextWindowTokens: plan.ContextWindowTokens,
            Normalization: plan.Normalization,
            QueryInstruction: null,
            RequiresTokenTypeIds: plan.RequiresTokenTypeIds,
            MRL: new MRLInfo(false, null),
            Pooling: new PoolingManifest(plan.PoolingMode,
                new PoolingOutputNames(plan.EmbeddingOutput ?? string.Empty, plan.TokenEmbeddingsOutput ?? string.Empty)),
            Tokenizer: new TokenizerManifest(plan.TokenizerFamily,
                [.. plan.TokenizerFiles.Select(f => new ManifestFile(TargetPath(f.Path, plan.ModelFilePath), Sha256Of(Path.Combine(targetDir, TargetPath(f.Path, plan.ModelFilePath)))))],
                new TokenizerOptionsManifest(plan.AddBeginOfSentence, plan.AddEndOfSentence, plan.SpecialTokens,
                    plan.VocabOffset)),
            Onnx: new OnnxManifest(plan.Inputs, plan.EmbeddingOutput, plan.TokenEmbeddingsOutput,
                [.. plan.ModelFiles.Select(f => new ManifestFile(TargetPath(f.Path, plan.ModelFilePath), Sha256Of(Path.Combine(targetDir, TargetPath(f.Path, plan.ModelFilePath)))))]));

        var errors = manifestValidator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new ModelDownloadException(
                "the download succeeded but the manifest failed validation — this is a bug: " + string.Join("; ", errors));
        }

        var path = Path.Combine(targetDir, EmbeddingManifest.FileName);
        await File.WriteAllTextAsync(path, manifestSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private string ResolveUrl(ModelDownloadRequest request, string path) =>
        $"{treeClient.Endpoint}/{request.RepoId}/resolve/{request.Revision}/{path}";

    /// <summary>On-disk path for a repo file: files inside the model's directory land flat (the
    /// plan §8.4 layout); external data in deeper subdirectories keeps its relative path so ONNX
    /// Runtime resolves it against the .onnx location.</summary>
    private static string TargetPath(string repoPath, string modelFilePath)
    {
        var modelDir = modelFilePath.Contains('/') ? modelFilePath[..modelFilePath.LastIndexOf('/')] : string.Empty;
        if (modelDir.Length > 0 && repoPath.StartsWith(modelDir + "/", StringComparison.Ordinal))
        {
            return repoPath[(modelDir.Length + 1)..];
        }

        return Path.GetFileName(repoPath);
    }

    private static void Cleanup(string targetDir, List<string> downloaded, bool createdTargetDir)
    {
        foreach (var path in downloaded)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best-effort
            }
        }

        if (createdTargetDir && Directory.Exists(targetDir) && !Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            try
            {
                Directory.Delete(targetDir);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal static class ModelDownloadPlanSize
{
    public static long TotalSize(this ModelDownloadPlan plan) => plan.Files.Sum(f => f.Size);
}
