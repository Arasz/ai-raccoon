using System.Text.Json;
using System.Text.Json.Nodes;

using AiRaccoon.Infrastructure.Embedding.Manifest;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     PROVISIONAL — WP3's manifest loader (D1/D5, M3/M4): reads <c>ai-raccoon.manifest.json</c>
///     from a model directory and validates it with actionable errors. Replaced at the join by
///     the canonical record + loader (WP1); do not extend its surface beyond the WP3 gate.
///     Validation is structural (family/pooling/dims/shape + file existence); per-file sha256
///     verification on every load is deliberately NOT done — the download verb verifies before the
///     manifest is written, and the engine fingerprint (D7) hashes the manifest itself, so a
///     tampered file changes the fingerprint and re-embeds (a tampered manifest is a tampered
///     engine — engineer doc §11).
/// </summary>
public interface IProvisionalManifestDescriptor
{
    EngineDescriptor Load(string modelDirectory);

    void RequireWp3Supported(EngineDescriptor descriptor);
}

/// <inheritdoc cref="IProvisionalManifestDescriptor" />
public sealed class ProvisionalManifestDescriptor : IProvisionalManifestDescriptor
{
    private static readonly HashSet<string> KnownFamilies = ["bert-wordpiece", "sentencepiece", "tokenizer-json"];
    private static readonly HashSet<string> KnownPoolingModes = ["mean", "cls", "model-output", "last-token"];
    private static readonly HashSet<string> KnownNormalizations = ["l2", "none"];

    public EngineDescriptor Load(string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(modelDirectory);
        var manifestPath = Path.Combine(modelDirectory, EmbeddingManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Configured embedding model directory '{modelDirectory}' has no {EmbeddingManifest.FileName}. " +
                $"A local model directory must contain a {EmbeddingManifest.FileName} describing its files, tokenizer, pooling and " +
                "dimensions — run 'ai-raccoon model download <repo-id>' to create one, or point embedding.model at a " +
                ".onnx file for the legacy path.");
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(manifestPath))
                   ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' is not valid JSON: {ex.Message}", ex);
        }

        var version = root["manifestVersion"]?.GetValue<int?>() ?? 0;
        if (version != 1)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares manifestVersion {version}; this build supports manifestVersion 1.");
        }

        var model = RequiredString(root, "model", manifestPath);
        var provider = root["provider"]?.GetValue<string>();
        if (provider is not null && !provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares provider '{provider}'; local model manifests must declare provider \"local\".");
        }

        var dimensions = RequiredInt(root, "dimensions", manifestPath);
        if (dimensions <= 0)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' declares dimensions {dimensions}; dimensions must be positive.");
        }

        var contextWindow = RequiredInt(root, "contextWindowTokens", manifestPath);
        if (contextWindow <= 0)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares contextWindowTokens {contextWindow}; the context window must be positive.");
        }

        var normalization = root["normalization"]?.GetValue<string>() ?? "l2";
        if (!KnownNormalizations.Contains(normalization))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares normalization '{normalization}'; supported values: l2, none.");
        }

        if (root["queryInstruction"] is JsonNode { } queryInstruction && queryInstruction.GetValueKind() != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares a queryInstruction; query-prefix instructions are not supported in this build.");
        }

        if (root["mrl"] is JsonObject { } mrl && mrl["supported"]?.GetValue<bool>() == true)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares mrl.supported=true; MRL truncation is not supported in this build.");
        }

        var tokenizer = RequiredObject(root, "tokenizer", manifestPath);
        var family = RequiredString(tokenizer, "family", manifestPath);
        if (!KnownFamilies.Contains(family))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares tokenizer family '{family}'; supported families: bert-wordpiece, sentencepiece " +
                "(tokenizer-json is recognized but not supported yet).");
        }

        if (family == "tokenizer-json")
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares tokenizer family 'tokenizer-json'; that family is recognized but not supported yet " +
                "(gated on an ML.Tokenizers capability check, plan D5). Use bert-wordpiece or sentencepiece.");
        }

        var tokenizerFiles = RequiredFileList(tokenizer, "files", manifestPath);
        var tokenizerFile = tokenizerFiles[0];

        SentencePieceTokenizerOptions? sentencePieceOptions = null;
        if (family == "sentencepiece")
        {
            sentencePieceOptions = ReadSentencePieceOptions(tokenizer, manifestPath);
        }

        var pooling = RequiredObject(root, "pooling", manifestPath);
        var poolingMode = RequiredString(pooling, "mode", manifestPath);
        if (!KnownPoolingModes.Contains(poolingMode))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares pooling.mode '{poolingMode}'; supported values: mean, cls, model-output, last-token.");
        }

        if (poolingMode == "last-token")
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares pooling.mode 'last-token'; that mode is recognized but has no consumer in this build.");
        }

        var onnx = RequiredObject(root, "onnx", manifestPath);
        var onnxFiles = RequiredFileList(onnx, "files", manifestPath);
        var inputs = onnx["inputs"] as JsonArray
                     ?? throw new InvalidOperationException($"Manifest '{manifestPath}' must declare onnx.inputs (model input names).");
        var inputNames = inputs.Select(item => item?.GetValue<string>()
                ?? throw new InvalidOperationException($"Manifest '{manifestPath}' has a non-string onnx.inputs entry."))
            .ToList();
        if (inputNames.Count == 0 || !inputNames.Contains("input_ids") || !inputNames.Contains("attention_mask"))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' onnx.inputs must include at least 'input_ids' and 'attention_mask'.");
        }

        var tokenEmbeddingsOutput = RequiredString(onnx, "tokenEmbeddingsOutput", manifestPath);
        var embeddingOutput = onnx["embeddingOutput"]?.GetValue<string>();
        if (poolingMode == "model-output" && string.IsNullOrWhiteSpace(embeddingOutput))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares pooling.mode 'model-output' but no onnx.embeddingOutput; " +
                "model-output pooling requires the graph to expose a pooled sentence embedding output (e.g. 'sentence_embedding').");
        }

        if (embeddingOutput is not null && poolingMode != "model-output")
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares onnx.embeddingOutput '{embeddingOutput}' but pooling.mode '{poolingMode}'; " +
                "the graph-provided output is only used by pooling.mode 'model-output'.");
        }

        var requiresTokenTypeIds = tokenizer["requiresTokenTypeIds"]?.GetValue<bool>() ?? true;

        var allFiles = tokenizerFiles.Concat(onnxFiles).ToList();
        foreach (var file in allFiles)
        {
            var fullPath = ResolveFile(modelDirectory, file.Path, manifestPath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"Manifest '{manifestPath}' declares file '{file.Path}' but it is missing from '{modelDirectory}'. " +
                    "Re-run 'ai-raccoon model download' to restore the model directory.");
            }
        }

        // D1 marks source required and the WP1 validator enforces repo+revision; the read path
        // agrees with the pinned contract rather than accepting a manifest the writer never emits.
        var source = root["source"] as JsonObject
                     ?? throw new InvalidOperationException(
                         $"Manifest '{manifestPath}' is missing required field 'source' (repo and revision).");
        return new EngineDescriptor(
            Model: model,
            SourceRepo: RequiredString(source, "repo", manifestPath),
            SourceRevision: RequiredString(source, "revision", manifestPath),
            Dimensions: dimensions,
            ContextWindowTokens: contextWindow,
            Normalization: normalization,
            Pooling: poolingMode,
            SpecialTokenReservation: EngineDescriptor.DefaultSpecialTokenReservation,
            TokenizerFamily: family,
            TokenizerFile: tokenizerFile.Path,
            SentencePieceOptions: sentencePieceOptions,
            RequiresTokenTypeIds: requiresTokenTypeIds,
            InputNames: inputNames,
            TokenEmbeddingsOutput: tokenEmbeddingsOutput,
            EmbeddingOutput: embeddingOutput,
            OnnxModelFile: onnxFiles[0].Path,
            Files: allFiles);
    }

    /// <summary>
    ///     WP3 activation gate (M4): only 384-dimension manifest models are loadable until the
    ///     dimension-reconcile work (WP4) lands. Everything else is refused with an actionable error.
    /// </summary>
    public void RequireWp3Supported(EngineDescriptor descriptor)
    {
        if (descriptor.Dimensions != 384)
        {
            throw new InvalidOperationException(
                $"Manifest model '{descriptor.Model}' declares {descriptor.Dimensions} dimensions; this build supports only " +
                "384-dimension local models (arbitrary-dimension support ships with the dimension-reconcile work). " +
                "Pick a 384-dimension model, or reset to the bundled default with 'ai-raccoon model reset'.");
        }
    }

    private static SentencePieceTokenizerOptions ReadSentencePieceOptions(JsonObject tokenizer, string manifestPath)
    {
        var options = tokenizer["options"] as JsonObject
                      ?? throw new InvalidOperationException(
                          $"Manifest '{manifestPath}' sentencepiece tokenizer must declare tokenizer.options " +
                          "(addBeginOfSentence, addEndOfSentence, specialTokens).");

        var addBos = options["addBeginOfSentence"]?.GetValue<bool>() ?? true;
        var addEos = options["addEndOfSentence"]?.GetValue<bool>() ?? false;

        IReadOnlyDictionary<string, int>? specialTokens = null;
        if (options["specialTokens"] is JsonObject specialMap)
        {
            specialTokens = specialMap.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.GetValue<int>()
                        ?? throw new InvalidOperationException(
                            $"Manifest '{manifestPath}' tokenizer.options.specialTokens entry '{pair.Key}' must map to an integer token id."));
        }
        else if (options["specialTokens"] is JsonValue { } specialValue && specialValue.GetValueKind() != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' tokenizer.options.specialTokens must be a NUMERIC map of token name to id " +
                "(e.g. {{\"<s>\": 0, \"<pad>\": 1, \"</s>\": 2, \"<unk>\": 3}}), taken from the model's special_tokens_map.json " +
                "or tokenizer_config.json at download time (D1).");
        }

        return new SentencePieceTokenizerOptions(addBos, addEos, specialTokens);
    }

    private static IReadOnlyList<ManifestFile> RequiredFileList(JsonObject parent, string property, string manifestPath)
    {
        var files = parent[property] as JsonArray
                    ?? throw new InvalidOperationException($"Manifest '{manifestPath}' must declare {property} (a non-empty file list).");
        var result = new List<ManifestFile>();
        foreach (var item in files)
        {
            if (item is not JsonObject file)
            {
                throw new InvalidOperationException($"Manifest '{manifestPath}' {property} entries must be objects with path and sha256.");
            }

            var path = RequiredString(file, "path", manifestPath);
            var sha256 = RequiredString(file, "sha256", manifestPath);
            if (!IsHexSha256(sha256))
            {
                throw new InvalidOperationException(
                    $"Manifest '{manifestPath}' declares file '{path}' with a malformed sha256 ('{sha256}'); expected 64 hex characters.");
            }

            result.Add(new ManifestFile(path, sha256.ToLowerInvariant()));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' {property} must not be empty.");
        }

        return result;
    }

    private static string ResolveFile(string modelDirectory, string relativePath, string manifestPath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares file '{relativePath}'; file paths must be relative to the model directory.");
        }

        return Path.Combine(modelDirectory, relativePath);
    }

    private static bool IsHexSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string RequiredString(JsonNode parent, string property, string manifestPath) =>
        parent[property]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is missing required field '{property}'.");

    private static int RequiredInt(JsonNode parent, string property, string manifestPath) =>
        parent[property]?.GetValue<int>()
        ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is missing required field '{property}'.");

    private static JsonObject RequiredObject(JsonNode parent, string property, string manifestPath) =>
        parent[property] as JsonObject
        ?? throw new InvalidOperationException($"Manifest '{manifestPath}' is missing required object '{property}'.");
}
