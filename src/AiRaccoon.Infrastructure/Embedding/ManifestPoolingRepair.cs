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
///     A manifest that cannot be read, or a graph that cannot be loaded, is left alone without a
///     line of its own: the caller's own <see cref="IEmbeddingManifestLoader" /> load runs next and
///     reports it with the actionable message that owns that failure.
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
            || manifest.Onnx.Files.Count == 0 || !GraphPools(modelDirectory, manifest, output))
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

        try
        {
            File.WriteAllText(manifestPath, serializer.Serialize(repaired));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.PoolingModeNotRepaired(logger, manifestPath, ex.Message);
            return false;
        }

        Log.PoolingModeRepaired(logger, manifest.Model, SchemaName(manifest.Pooling.Mode), output, manifestPath);
        return true;
    }

    private bool GraphPools(string modelDirectory, EmbeddingManifest manifest, string output)
    {
        try
        {
            return graph.Verify(Path.Combine(modelDirectory, manifest.Onnx.Files[0].Path))
                .TryGetValue(output, out var rank) && rank == OnnxOutputRanks.PooledRank;
        }
        catch (OnnxSmokeTestException)
        {
            return false;
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
