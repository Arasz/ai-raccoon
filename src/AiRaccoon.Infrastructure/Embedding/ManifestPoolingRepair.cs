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
///     Repairs a token-level <c>pooling.mode</c> over an output the graph has already pooled: the
///     #470 shape (the sole output serves both roles) and the #497 shape (a distinctly-named
///     <c>onnx.embeddingOutput</c> the planner always name-selected, but whose real rank was never
///     checked before #496). Runs at engine ACTIVATION, never at load — activation invalidates
///     every vector anyway, so the fingerprint change this rewrite causes costs nothing there,
///     while the same rewrite on a load would re-embed a whole corpus for a correction that
///     changes no vector.
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

        var tokenOutput = manifest.Onnx.TokenEmbeddingsOutput;
        var embeddingOutput = manifest.Onnx.EmbeddingOutput;
        if (manifest.Pooling.Mode == PoolingMode.ModelOutput || manifest.Onnx.Files.Count == 0
            || (string.IsNullOrWhiteSpace(tokenOutput) && string.IsNullOrWhiteSpace(embeddingOutput)))
        {
            return false;
        }

        var ranks = GraphOutputRanks(modelDirectory, manifestPath, manifest);
        if (ranks is null)
        {
            return false;
        }

        // #470: the sole output the manifest names as token-level already serves both roles.
        if (!string.IsNullOrWhiteSpace(tokenOutput) && GraphPools(ranks, tokenOutput))
        {
            return WriteRepair(manifestPath, manifest, tokenOutput, manifest.Pooling.OutputNames?.TokenEmbeddings ?? tokenOutput);
        }

        // #497: a distinctly-named embeddingOutput the graph itself pools, beside a token-level
        // output that stays genuinely token-level — both names are left exactly as they are.
        if (!string.IsNullOrWhiteSpace(embeddingOutput) && embeddingOutput != tokenOutput && GraphPools(ranks, embeddingOutput))
        {
            return WriteRepair(manifestPath, manifest, embeddingOutput, tokenOutput ?? embeddingOutput);
        }

        return false;
    }

    /// <summary>Writes the repaired manifest: <paramref name="embeddingOutputName" /> becomes
    /// <c>onnx.embeddingOutput</c>, <paramref name="outputNamesTokenEmbeddings" /> becomes
    /// <c>pooling.outputNames.tokenEmbeddings</c>. Shared by both repair shapes.</summary>
    private bool WriteRepair(string manifestPath, EmbeddingManifest manifest, string embeddingOutputName, string outputNamesTokenEmbeddings)
    {
        var repaired = manifest with
        {
            Pooling = new PoolingManifest(PoolingMode.ModelOutput,
                new PoolingOutputNames(embeddingOutputName, outputNamesTokenEmbeddings)),
            Onnx = manifest.Onnx with { EmbeddingOutput = embeddingOutputName }
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

        Log.PoolingModeRepaired(logger, manifest.Model, SchemaName(repaired.Pooling.Mode), embeddingOutputName, manifestPath);
        return true;
    }

    private IReadOnlyDictionary<string, int>? GraphOutputRanks(string modelDirectory, string manifestPath, EmbeddingManifest manifest)
    {
        try
        {
            return graph.Verify(Path.Combine(modelDirectory, manifest.Onnx.Files[0].Path));
        }
        catch (OnnxSmokeTestException ex)
        {
            // Nothing else on the activation path opens the graph, so this is the only chance to
            // say why the manifest could not be checked at all.
            Log.PoolingModeNotRepaired(logger, manifestPath, ex.Message);
            return null;
        }
    }

    private static bool GraphPools(IReadOnlyDictionary<string, int> ranks, string output) =>
        ranks.TryGetValue(output, out var rank) && rank == OnnxOutputRanks.PooledRank;

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
