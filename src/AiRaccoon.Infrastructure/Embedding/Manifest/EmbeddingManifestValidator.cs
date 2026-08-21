using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Embedding.Manifest;

/// <summary>
///     Semantic validation of a parsed manifest (plan D5, architecture §5.2): every rejection is
///     actionable — it names the offending field path and the accepted values. Pure function: no
///     I/O, no exceptions; returns every error, not just the first.
/// </summary>
public static class EmbeddingManifestValidator
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(EmbeddingManifest manifest)
    {
        var errors = new List<string>();
        if (manifest is null)
        {
            return ["manifest: the manifest is null"];
        }

        if (manifest.ManifestVersion != 1)
        {
            errors.Add($"manifestVersion: unsupported manifest version {manifest.ManifestVersion} (only version 1 is pinned)");
        }

        ValidateProvider(manifest, errors);

        var isLocal = manifest.Provider == ManifestProvider.Local;

        if (string.IsNullOrWhiteSpace(manifest.Model))
        {
            errors.Add("model: must name the model (e.g. 'BAAI/bge-m3')");
        }

        ValidateSource(manifest.Source, errors);

        if (manifest.Dimensions <= 0)
        {
            errors.Add($"dimensions: must be a positive integer, got {manifest.Dimensions}");
        }

        if (manifest.ContextWindowTokens <= 0)
        {
            errors.Add($"contextWindowTokens: must be a positive integer, got {manifest.ContextWindowTokens}");
        }

        if (!Enum.IsDefined(manifest.Normalization))
        {
            errors.Add($"normalization: unknown mode '{manifest.Normalization}' (expected l2 or none)");
        }

        ValidateMRL(manifest.MRL, errors);
        ValidatePooling(manifest.Pooling, manifest.Onnx, errors);
        ValidateTokenizer(manifest.Tokenizer, isLocal, errors);
        ValidateOnnx(manifest.Onnx, isLocal, errors);

        return errors;
    }

    private static void ValidateProvider(EmbeddingManifest manifest, List<string> errors)
    {
        // Whether the provider matches the settings row is a runtime check at activation (WP3);
        // the manifest contract itself only pins the two legal values.
        if (!Enum.IsDefined(manifest.Provider))
        {
            errors.Add($"provider: unknown provider '{manifest.Provider}' (expected local or openai)");
        }
    }

    private static void ValidateSource(ManifestSource? source, List<string> errors)
    {
        if (source is null)
        {
            errors.Add("source: must name the origin repo and revision");
            return;
        }

        if (string.IsNullOrWhiteSpace(source.Repo))
        {
            errors.Add("source.repo: must name the Hugging Face repo (e.g. 'BAAI/bge-m3')");
        }

        if (string.IsNullOrWhiteSpace(source.Revision))
        {
            errors.Add("source.revision: must name the resolved revision (e.g. 'main')");
        }
    }

    private static void ValidateMRL(MRLInfo? mrl, List<string> errors)
    {
        if (mrl is null)
        {
            errors.Add("mrl: must declare { supported, minDimensions }");
            return;
        }

        if (mrl.Supported && mrl.MinDimensions is not > 0)
        {
            errors.Add("mrl.supported: 'true' requires mrl.minDimensions — the smallest MRL output dimension the model can truncate to (never assumed)");
        }
    }

    private static void ValidatePooling(PoolingManifest? pooling, OnnxManifest? onnx, List<string> errors)
    {
        if (pooling is null)
        {
            errors.Add("pooling: must declare a mode and output names");
            return;
        }

        if (!Enum.IsDefined(pooling.Mode))
        {
            errors.Add($"pooling.mode: unknown mode '{pooling.Mode}' (expected mean, cls, model-output or last-token)");
            return;
        }

        if (pooling.OutputNames is null)
        {
            errors.Add("pooling.outputNames: must name the embedding and tokenEmbeddings graph outputs");
            return;
        }

        if (pooling.Mode == PoolingMode.ModelOutput
            && string.IsNullOrWhiteSpace(onnx?.EmbeddingOutput))
        {
            errors.Add("pooling.mode: 'model-output' requires onnx.embeddingOutput to name the graph's pooled output (e.g. 'sentence_embedding')");
        }

        if (pooling.Mode is PoolingMode.Mean or PoolingMode.Cls or PoolingMode.LastToken
            && string.IsNullOrWhiteSpace(onnx?.TokenEmbeddingsOutput))
        {
            errors.Add($"pooling.mode: '{Kebab(pooling.Mode)}' requires onnx.tokenEmbeddingsOutput to name the token-level output (e.g. 'token_embeddings' or 'last_hidden_state')");
        }
    }

    private static void ValidateTokenizer(TokenizerManifest? tokenizer, bool isLocal, List<string> errors)
    {
        if (tokenizer is null)
        {
            errors.Add("tokenizer: must declare a family, files and options");
            return;
        }

        if (!Enum.IsDefined(tokenizer.Family))
        {
            errors.Add($"tokenizer.family: unknown family '{tokenizer.Family}' (expected bert-wordpiece, sentencepiece or tokenizer-json)");
            return;
        }

        if (tokenizer.Family == TokenizerFamily.TokenizerJson)
        {
            errors.Add("tokenizer.family: 'tokenizer-json' is not yet supported (D5 capability gate — deferred until ML.Tokenizers can consume HF tokenizer.json)");
        }

        ValidateFileList("tokenizer.files", tokenizer.Files, isLocal, errors);

        if (tokenizer.Options is null)
        {
            errors.Add("tokenizer.options: must declare addBeginOfSentence, addEndOfSentence and specialTokens");
            return;
        }

        if (isLocal && tokenizer.Options.SpecialTokens.Count == 0)
        {
            errors.Add("tokenizer.options.specialTokens: must map special-token names to numeric ids taken from tokenizer_config.json (never a guessed mask mapping)");
        }

        foreach (var (name, id) in tokenizer.Options.SpecialTokens)
        {
            if (id < 0)
            {
                errors.Add($"tokenizer.options.specialTokens['{name}']: token id must be non-negative, got {id}");
            }
        }
    }

    private static void ValidateOnnx(OnnxManifest? onnx, bool isLocal, List<string> errors)
    {
        if (onnx is null)
        {
            errors.Add("onnx: must declare inputs, outputs and files");
            return;
        }

        if (isLocal && onnx.Inputs.Count == 0)
        {
            errors.Add("onnx.inputs: must name the graph inputs (e.g. 'input_ids', 'attention_mask')");
        }

        ValidateFileList("onnx.files", onnx.Files, isLocal, errors);
    }

    private static void ValidateFileList(string fieldPath, IReadOnlyList<ManifestFile>? files, bool isLocal, List<string> errors)
    {
        if (files is null)
        {
            errors.Add($"{fieldPath}: must list the pinned files");
            return;
        }

        if (isLocal && files.Count == 0)
        {
            errors.Add($"{fieldPath}: provider 'local' requires at least one pinned file");
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                errors.Add($"{fieldPath}[{i}].path: must not be empty");
            }

            if (!Sha256Pattern.IsMatch(file.Sha256 ?? string.Empty))
            {
                errors.Add($"{fieldPath}[{i}].sha256: must be a 64-character hex SHA-256, got '{file.Sha256}'");
            }
        }
    }

    private static string Kebab(PoolingMode mode) =>
        Enum.GetName(mode)!.Replace('_', '-').ToLowerInvariant();
}
