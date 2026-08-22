using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Corrects an on-disk manifest whose pooling mode the model's own graph makes unappliable.</summary>
public interface IManifestPoolingRepair
{
    /// <summary>Rewrites the pooling block to 'model-output' when the graph pools itself; true when
    /// the manifest changed.</summary>
    bool Repair(string modelDirectory);
}

/// <summary>
///     Repairs the #470 manifest: a token-level <c>pooling.mode</c> over an output the graph has
///     already pooled. Runs at engine ACTIVATION, never at load — activation invalidates every
///     vector anyway, so the fingerprint change this rewrite causes costs nothing there, while the
///     same rewrite on a load would re-embed a whole corpus for a correction that changes no vector.
/// </summary>
/// <remarks>
///     A manifest that cannot be read is left alone silently: the caller's own
///     <see cref="IEmbeddingManifestLoader" /> load owns that failure and reports it with an
///     actionable message. A graph that cannot be LOADED is a different case — nothing else on this
///     path opens the graph, so that one is reported here (event 425) rather than swallowed.
/// </remarks>
public sealed partial class ManifestPoolingRepair(
    IEmbeddingManifestSerializer serializer,
    IEmbeddingManifestValidator validator,
    IOnnxSmokeTester graph,
    ILogger<ManifestPoolingRepair> logger) : IManifestPoolingRepair
{
    public bool Repair(string modelDirectory)
    {
        Guard.IsNotNullOrWhiteSpace(modelDirectory);
        var manifestPath = Path.Combine(modelDirectory, EmbeddingManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        EmbeddingManifest manifest;
        try
        {
            manifest = serializer.Deserialize(File.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is EmbeddingManifestFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var output = manifest.Onnx.TokenEmbeddingsOutput;
        if (manifest.Pooling.Mode == PoolingMode.ModelOutput || string.IsNullOrWhiteSpace(output)
            || manifest.Onnx.Files.Count == 0 || !GraphPools(modelDirectory, manifestPath, manifest, output))
        {
            return false;
        }

        var repaired = manifest with
        {
            Pooling = new PoolingManifest(PoolingMode.ModelOutput,
                new PoolingOutputNames(output, manifest.Pooling.OutputNames?.TokenEmbeddings ?? output)),
            Onnx = manifest.Onnx with { EmbeddingOutput = output }
        };

        var errors = validator.Validate(repaired);
        if (errors.Count > 0)
        {
            Log.PoolingModeNotRepaired(logger, manifestPath, string.Join("; ", errors));
            return false;
        }

        // Sibling temp file + atomic move: this is the user's installed model, and a crash midway
        // through an in-place write would leave a truncated manifest nothing on this path detects.
        // The rename would happily replace a read-only manifest — an in-place write could not — so
        // that intent is honoured explicitly rather than lost to the change of mechanism.
        var temporaryPath = manifestPath + ".repair.tmp";
        try
        {
            if (File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReadOnly))
            {
                throw new UnauthorizedAccessException($"'{manifestPath}' is read-only");
            }

            File.WriteAllText(temporaryPath, serializer.Serialize(repaired));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Delete(temporaryPath);
            Log.PoolingModeNotRepaired(logger, manifestPath, ex.Message);
            return false;
        }

        Log.PoolingModeRepaired(logger, manifest.Model, SchemaName(manifest.Pooling.Mode), output, manifestPath);
        return true;
    }

    private bool GraphPools(string modelDirectory, string manifestPath, EmbeddingManifest manifest, string output)
    {
        try
        {
            return graph.Verify(Path.Combine(modelDirectory, manifest.Onnx.Files[0].Path))
                .TryGetValue(output, out var rank) && rank == OnnxOutputRanks.PooledRank;
        }
        catch (OnnxSmokeTestException ex)
        {
            // Nothing else on the activation path opens the graph, so this is the only chance to
            // say why the manifest could not be checked at all.
            Log.PoolingModeNotRepaired(logger, manifestPath, ex.Message);
            return false;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort: the run already failed on the original exception, which is what is logged
        }
    }

    private static string SchemaName(PoolingMode mode) => KebabCaseEnumJsonConverter<PoolingMode>.SchemaNameOf(mode);

    public static partial class Log
    {
        /// <summary>#470: the manifest the download wrote before it read the rank, corrected once.</summary>
        [LoggerMessage(EventId = 424, Level = LogLevel.Information,
            Message = "Model '{Model}' pools inside its own ONNX graph, so its manifest's pooling mode "
                      + "'{Pooling}' could never be applied: '{Manifest}' now says 'model-output' with "
                      + "embeddingOutput '{Output}'. Embedding is unchanged — the vectors this engine "
                      + "produced before and after this correction are the same.")]
        public static partial void PoolingModeRepaired(ILogger logger, string model, string pooling, string output, string manifest);

        /// <summary>The correction could not be written; event 417 keeps firing on every load until it can.</summary>
        [LoggerMessage(EventId = 425, Level = LogLevel.Warning,
            Message = "Manifest '{Manifest}' declares a pooling mode its own ONNX graph makes unappliable "
                      + "and could not be corrected: {Reason}. Embedding is unaffected (the graph's own "
                      + "vector is used), but event 417 will warn on every load until the file is fixed.")]
        public static partial void PoolingModeNotRepaired(ILogger logger, string manifest, string reason);
    }
}
