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

        // #470: the sole output the manifest names as token-level already serves both roles —
        // every embed already reads it either way, so the correction changes no vector.
        if (!string.IsNullOrWhiteSpace(tokenOutput) && GraphPools(ranks, tokenOutput))
        {
            var repaired = Repaired(manifest, tokenOutput, tokenOutput);
            if (!WriteManifest(manifestPath, repaired, out var reason))
            {
                Log.PoolingModeNotRepaired(logger, manifestPath, reason!);
                return false;
            }

            Log.PoolingModeRepaired(logger, manifest.Model, SchemaName(manifest.Pooling.Mode), tokenOutput, manifestPath);
            return true;
        }

        // #497: a distinctly-named embeddingOutput the graph itself pools, beside a token-level
        // output that stays genuinely token-level (rank 3) — every embed until now read THAT
        // output, so the correction switches which tensor is read and changes the vectors.
        if (!string.IsNullOrWhiteSpace(embeddingOutput) && embeddingOutput != tokenOutput && GraphPools(ranks, embeddingOutput))
        {
            var repaired = Repaired(manifest, embeddingOutput, tokenOutput ?? embeddingOutput);
            if (!WriteManifest(manifestPath, repaired, out var reason))
            {
                Log.DistinctPoolingModeNotRepaired(logger, manifestPath, tokenOutput ?? embeddingOutput, reason!);
                return false;
            }

            Log.DistinctPoolingModeRepaired(logger, manifest.Model, SchemaName(manifest.Pooling.Mode), embeddingOutput, tokenOutput ?? embeddingOutput, manifestPath);
            return true;
        }

        return false;
    }

    /// <summary>The rewritten manifest: <paramref name="embeddingOutputName" /> becomes
    /// <c>onnx.embeddingOutput</c>. <c>pooling.outputNames.tokenEmbeddings</c> is preserved from
    /// whatever the manifest already said (falling back to <paramref name="tokenOutput" /> only
    /// when unset) by the SAME rule for both repair shapes — neither one overwrites it.</summary>
    private static EmbeddingManifest Repaired(EmbeddingManifest manifest, string embeddingOutputName, string tokenOutput) =>
        manifest with
        {
            Pooling = new PoolingManifest(PoolingMode.ModelOutput,
                new PoolingOutputNames(embeddingOutputName, manifest.Pooling.OutputNames?.TokenEmbeddings ?? tokenOutput)),
            Onnx = manifest.Onnx with { EmbeddingOutput = embeddingOutputName }
        };

    /// <summary>Validates then writes <paramref name="repaired" /> in place; <paramref name="failureReason" />
    /// names why on a false return. Shared by both repair shapes — each call site logs its own
    /// truthful message with the id its shape owns.</summary>
    private bool WriteManifest(string manifestPath, EmbeddingManifest repaired, out string? failureReason)
    {
        var errors = validator.Validate(repaired);
        if (errors.Count > 0)
        {
            failureReason = string.Join("; ", errors);
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
            failureReason = ex.Message;
            return false;
        }

        failureReason = null;
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
        /// <summary>#470: the manifest's sole output already served both roles, corrected once.
        /// Relocated from 424 (2026-08-23, #497/#504 review): a second, truthful pair was needed
        /// for the #497 shape below, and the gate forbids a second block per owner.</summary>
        [LoggerMessage(EventId = 429, Level = LogLevel.Information,
            Message = "Model '{Model}' pools inside its own ONNX graph, so its manifest's pooling mode "
                      + "'{Pooling}' could never be applied: '{Manifest}' now says 'model-output' with "
                      + "embeddingOutput '{Output}'. Embedding is unchanged — the vectors this engine "
                      + "produced before and after this correction are the same.")]
        public static partial void PoolingModeRepaired(ILogger logger, string model, string pooling, string output, string manifest);

        /// <summary>The #470-shape correction could not be written; event 417 keeps firing on every
        /// load until it can (relocated from 425, same review as above).</summary>
        [LoggerMessage(EventId = 430, Level = LogLevel.Warning,
            Message = "Manifest '{Manifest}' declares a pooling mode its own ONNX graph makes unappliable "
                      + "and could not be corrected: {Reason}. Embedding is unaffected (the graph's own "
                      + "vector is used), but event 417 will warn on every load until the file is fixed.")]
        public static partial void PoolingModeNotRepaired(ILogger logger, string manifest, string reason);

        /// <summary>
        ///     #497: unlike 429's sole-output shape, every embed until now read
        ///     <paramref name="tokenOutput" /> (a genuine, still-token-level tensor) under the wrong
        ///     mode — the correction switches the read to the graph's own pooled
        ///     <paramref name="output" />, a DIFFERENT tensor, so existing vectors change and the
        ///     engine's fingerprint change (D7, ADR-0084) will re-embed.
        /// </summary>
        [LoggerMessage(EventId = 431, Level = LogLevel.Information,
            Message = "Model '{Model}' also pools '{Output}' beside its genuine token-level output "
                      + "'{TokenOutput}': pooling mode '{Pooling}' was applied on every embed, but over "
                      + "'{TokenOutput}', not the graph's own pooled vector. '{Manifest}' now says "
                      + "'model-output' with embeddingOutput '{Output}' — this reads a different tensor, "
                      + "so existing vectors change and a re-embed follows.")]
        public static partial void DistinctPoolingModeRepaired(ILogger logger, string model, string pooling, string output, string tokenOutput, string manifest);

        /// <summary>
        ///     #497: unlike 430, event 417 only fires when the output actually read is itself
        ///     rank-2 — here it stays <paramref name="tokenOutput" />, a genuine rank-3 tensor, so
        ///     417 never catches this and a failed correction is silently permanent.
        /// </summary>
        [LoggerMessage(EventId = 432, Level = LogLevel.Warning,
            Message = "Manifest '{Manifest}' names a distinctly-pooled output beside its genuine "
                      + "token-level output '{TokenOutput}', but the correction could not be written: "
                      + "{Reason}. Event 417 will NOT warn about this — it only fires when the output "
                      + "actually read is itself rank-2, and '{TokenOutput}' is not — so the wrong "
                      + "pooling mode is now silently permanent until the manifest is fixed by hand or "
                      + "the model is re-downloaded.")]
        public static partial void DistinctPoolingModeNotRepaired(ILogger logger, string manifest, string tokenOutput, string reason);
    }
}
