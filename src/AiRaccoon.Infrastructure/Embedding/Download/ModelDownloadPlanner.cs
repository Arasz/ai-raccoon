using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding.Manifest;

namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>A file the download will fetch: repo-relative path, size (from the tree listing),
/// and the SHA-256 pin when the file is an LFS object (lfs.oid captured before download, D8).
/// A null sha means TOFU: pinned from the bytes actually downloaded and recorded in the manifest.</summary>
public sealed record PinnedFile(string Path, long Size, string? LfsSha256);

/// <summary>
///     Everything the downloader needs: the file set with pins, the tokenizer pairing, dims/ctx,
///     special-token ids, and the pooling decision (D11: sentence-transformers provenance when
///     <c>1_Pooling</c> is present, else a WP5 placeholder — either way, a graph that bakes its
///     own second pooled output outranks the repo's flags, issue #459). <c>SpecialTokensPending</c> is true
///     when a sentencepiece repo ships no <c>added_tokens_decoder</c> and ids still need deriving
///     from the sp model's own piece table once it is on disk (issue #417, D1-compatible).
/// </summary>
public sealed record ModelDownloadPlan(
    string RepoId,
    string Revision,
    IReadOnlyList<PinnedFile> Files,
    IReadOnlyList<PinnedFile> ModelFiles,
    IReadOnlyList<PinnedFile> TokenizerFiles,
    IReadOnlyList<PinnedFile> ProvenanceFiles,
    string ModelFilePath,
    TokenizerFamily TokenizerFamily,
    int Dimensions,
    int ContextWindowTokens,
    bool RequiresTokenTypeIds,
    IReadOnlyList<string> Inputs,
    string? EmbeddingOutput,
    string? TokenEmbeddingsOutput,
    PoolingMode PoolingMode,
    NormalizationMode Normalization,
    bool AddBeginOfSentence,
    bool AddEndOfSentence,
    IReadOnlyDictionary<string, int> SpecialTokens,
    string PoolingProvenance,
    int VocabOffset = 0,
    bool SpecialTokensPending = false);

/// <summary>A repo cannot be turned into a download plan: missing model file, unsupported
/// tokenizer family, unpinnable special tokens, etc. Messages are actionable.</summary>
public sealed class ModelDownloadPlanException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns a resolved tree + fetched configs into a download plan (pure, no I/O).</summary>
public interface IModelDownloadPlanner
{
    /// <summary>The auto-selected (or --file) model path; throws when no ONNX file is present.</summary>
    string SelectModelFilePath(IReadOnlyList<HfTreeEntry> tree, IReadOnlyList<string>? explicitFiles = null);

    ModelDownloadPlan BuildPlan(
        string repoId,
        string revision,
        IReadOnlyList<HfTreeEntry> tree,
        IReadOnlyDictionary<string, string> rawFiles,
        OnnxGraphProbe? probe,
        IReadOnlyList<string>? explicitFiles = null,
        IReadOnlyDictionary<string, int>? sentencePieceVocabulary = null);
}

/// <summary>
///     Pure planning over the resolved tree + fetched configs (plan §8.2 steps 2-4): auto-selects
///     <c>onnx/model.onnx</c> else root <c>model.onnx</c>, enumerates external-data siblings from
///     the ONNX protobuf probe (glob <c>*.onnx_data*</c> fallback when the probe is absent, e.g.
///     --dry-run), pairs the tokenizer from <c>config.json</c> model_type, derives dims/ctx from
///     <c>hidden_size</c>/<c>max_position_embeddings − 2</c>, and pins numeric special-token ids
///     from <c>tokenizer_config.json</c>'s added_tokens_decoder — never a guessed mask mapping (D1).
///     A sentencepiece repo without a decoder defers to the sp model's own piece table instead of
///     refusing outright (issue #417); every other family is unaffected. Whether that repo is
///     fairseq-offset is measured from config.json's vocab_size against the piece table itself, not
///     the tokenizer_class string (issue #423's false-refusal fix) — a real difference still refuses,
///     naming the measured numbers. Stateless and injectable.
/// </summary>
public sealed class ModelDownloadPlanner : IModelDownloadPlanner
{
    private static readonly string[] SupportedModelTypes =
        ["bert*", "xlm-roberta", "roberta", "t5", "gpt2", "llama", "qwen2"];

    public ModelDownloadPlan BuildPlan(
        string repoId,
        string revision,
        IReadOnlyList<HfTreeEntry> tree,
        IReadOnlyDictionary<string, string> rawFiles,
        OnnxGraphProbe? probe,
        IReadOnlyList<string>? explicitFiles = null,
        IReadOnlyDictionary<string, int>? sentencePieceVocabulary = null)
    {
        var modelFilePath = SelectModelFilePath(tree, explicitFiles);
        var modelDir = DirectoryOf(modelFilePath);

        var modelFiles = SelectModelFiles(tree, modelFilePath, modelDir, probe);

        var configPath = ConfigPath(tree, modelDir, "config.json", "config.json is the dims/ctx source");
        var tokenizerConfigPath = ConfigPath(tree, modelDir, "tokenizer_config.json", "tokenizer_config.json is the special-token id source");
        var configJson = RequireRaw(rawFiles, configPath);
        var tokenizerConfigJson = RequireRaw(rawFiles, tokenizerConfigPath);

        var (family, tokenizerFileName) = PairTokenizer(configJson);
        var tokenizerFilePath = TokenizerFilePath(tree, modelDir, tokenizerFileName, family);
        var hasAddedTokensDecoder = HasAddedTokensDecoder(tokenizerConfigJson);
        var vocabOffset = ResolveVocabOffset(configJson, tokenizerConfigJson, family, hasAddedTokensDecoder, sentencePieceVocabulary);
        var (specialTokens, specialTokensPending) = ResolveSpecialTokens(tokenizerConfigJson, family, sentencePieceVocabulary);
        // HF puts the wrapper behaviour in the tokenizer CLASS, not the config: xlm-roberta's
        // tokenizer_config.json declares neither flag, yet the tokenizer always emits <s> … </s>.
        // Defaulting those to false embeds a token sequence the model never saw in training.
        var wrapsByDefault = WrapsWithBosEos(tokenizerConfigJson);
        var addBos = ReadBool(tokenizerConfigJson, "add_bos_token") ?? ReadBool(tokenizerConfigJson, "add_bos") ?? wrapsByDefault;
        var addEos = ReadBool(tokenizerConfigJson, "add_eos_token") ?? ReadBool(tokenizerConfigJson, "add_eos") ?? wrapsByDefault;

        var dimensions = RequiredInt(configJson, "hidden_size", "dimensions");
        var contextWindowTokens = RequiredInt(configJson, "max_position_embeddings", "contextWindowTokens") - 2;
        if (contextWindowTokens <= 0)
        {
            throw new ModelDownloadPlanException(
                $"config.json max_position_embeddings must be > 2 to derive a positive contextWindowTokens, got {contextWindowTokens + 2}");
        }

        var (poolingMode, normalization, poolingProvenance) = PoolingDecision(rawFiles, probe);

        var tokenizerEntry = TreeEntry(tree, tokenizerFilePath)
            ?? throw new ModelDownloadPlanException(
                $"the repo has no tokenizer file named '{tokenizerFileName}' (model_type '{ModelType(configJson)}'); expected it at '{tokenizerFilePath}' or '{tokenizerFileName}'");
        var tokenizerFiles = new List<PinnedFile> { Pinned(tree, tokenizerFilePath) };

        var provenanceFiles = new List<PinnedFile> { Pinned(tree, configPath), Pinned(tree, tokenizerConfigPath) };
        var allFiles = modelFiles.Concat(tokenizerFiles).Concat(provenanceFiles)
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var inputs = probe?.InputNames ?? [];
        var tokenEmbeddingsOutput = probe is null ? null : SelectTokenEmbeddingsOutput(probe.OutputNames);
        var embeddingOutput = probe is null ? null : SelectEmbeddingOutput(probe.OutputNames, tokenEmbeddingsOutput);

        return new ModelDownloadPlan(
            repoId,
            revision,
            allFiles,
            modelFiles,
            tokenizerFiles,
            provenanceFiles,
            modelFilePath,
            family,
            dimensions,
            contextWindowTokens,
            inputs.Contains("token_type_ids", StringComparer.Ordinal),
            inputs,
            embeddingOutput,
            tokenEmbeddingsOutput,
            poolingMode,
            normalization,
            addBos,
            addEos,
            specialTokens,
            poolingProvenance,
            vocabOffset,
            specialTokensPending);
    }

    public string SelectModelFilePath(IReadOnlyList<HfTreeEntry> tree, IReadOnlyList<string>? explicitFiles = null)
    {
        if (explicitFiles is { Count: > 0 })
        {
            foreach (var path in explicitFiles)
            {
                if (TreeEntry(tree, path) is null)
                {
                    throw new ModelDownloadPlanException($"--file '{path}' is not in the repo tree");
                }
            }

            return explicitFiles[0];
        }

        if (TreeEntry(tree, "onnx/model.onnx") is not null)
        {
            return "onnx/model.onnx";
        }

        if (TreeEntry(tree, "model.onnx") is not null)
        {
            return "model.onnx";
        }

        throw new ModelDownloadPlanException(
            "no ONNX model found: expected 'onnx/model.onnx' or 'model.onnx' in the repo tree. " +
            "Use --file to name a specific model file, or pick a repo with an ONNX export.");
    }

    /// <summary>Model file + every external-data sibling: from the ONNX protobuf when available
    /// (the honest source, D4 m6), glob <c>*.onnx_data*</c> in the same directory as fallback
    /// (the bge-m3 case, plan §8.2 step 2).</summary>
    private static List<PinnedFile> SelectModelFiles(
        IReadOnlyList<HfTreeEntry> tree, string modelFilePath, string modelDir, OnnxGraphProbe? probe)
    {
        var paths = new List<string> { modelFilePath };

        if (probe is not null)
        {
            foreach (var location in probe.ExternalDataFiles)
            {
                var path = JoinPath(modelDir, location);
                if (TreeEntry(tree, path) is null)
                {
                    throw new ModelDownloadPlanException(
                        $"external data file '{location}' is declared by the ONNX graph but missing from the repo tree (expected '{path}')");
                }

                paths.Add(path);
            }
        }
        else
        {
            foreach (var entry in tree.Where(e => e.Type == "file" && DirectoryOf(e.Path) == modelDir
                                                  && e.Path != modelFilePath
                                                  && Path.GetFileName(e.Path).Contains(".onnx_data", StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(entry.Path);
            }
        }

        return paths.Distinct(StringComparer.Ordinal).Select(p => Pinned(tree, p)).ToList();
    }

    private static (TokenizerFamily Family, string FileName) PairTokenizer(string configJson)
    {
        var modelType = ModelType(configJson);
        if (modelType.StartsWith("bert", StringComparison.OrdinalIgnoreCase))
        {
            return (TokenizerFamily.BertWordpiece, "vocab.txt");
        }

        if (modelType is "xlm-roberta" or "roberta" or "t5" || modelType.StartsWith("roberta-", StringComparison.OrdinalIgnoreCase))
        {
            return (TokenizerFamily.SentencePiece, modelType == "t5" ? "spiece.model" : "sentencepiece.bpe.model");
        }

        if (modelType is "gpt2" or "gpt_neo" or "gpt_neox" or "llama" or "qwen2"
            || modelType.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
            || modelType.StartsWith("qwen2-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelDownloadPlanException(
                $"model_type '{modelType}' pairs with the tokenizer-json family (HF tokenizer.json), which is not yet supported (D5 capability gate — deferred until ML.Tokenizers can consume HF tokenizer.json)");
        }

        throw new ModelDownloadPlanException(
            $"unsupported model_type '{modelType}' in config.json; supported model types: {string.Join(", ", SupportedModelTypes)}. " +
            "Download a model with one of these types, or hand-place the model and write its manifest yourself.");
    }

    private static string TokenizerFilePath(IReadOnlyList<HfTreeEntry> tree, string modelDir, string fileName, TokenizerFamily family)
    {
        var besideModel = JoinPath(modelDir, fileName);
        if (TreeEntry(tree, besideModel) is not null)
        {
            return besideModel;
        }

        if (TreeEntry(tree, fileName) is not null)
        {
            return fileName;
        }

        throw new ModelDownloadPlanException(
            $"no tokenizer file found for family '{family}': expected '{besideModel}' or '{fileName}' in the repo tree");
    }

    /// <summary>
    ///     Numeric special-token ids for the tokens actually declared (bos/eos/unk/pad/cls/sep). No
    ///     &lt;mask&gt; mapping — its id is model-specific (xlm-roberta: 250001) and must never be
    ///     guessed (D1).
    /// </summary>
    /// <remarks>
    ///     tokenizer_config.json's own <c>added_tokens_decoder</c> is the primary source. When it is
    ///     absent AND the tokenizer family is sentencepiece, ids are instead derivable from the sp
    ///     model file's own piece table (issue #417, D1-compatible option 2) — but that file is not
    ///     on disk yet when the plan is built pre-download, so resolution is deferred
    ///     (<c>Pending</c> true, empty map) until the caller supplies <paramref name="sentencePieceVocabulary" />
    ///     from the downloaded file. Every other family is unaffected: no piece table, no fallback.
    /// </remarks>
    private static (IReadOnlyDictionary<string, int> Tokens, bool Pending) ResolveSpecialTokens(
        string tokenizerConfigJson, TokenizerFamily family, IReadOnlyDictionary<string, int>? sentencePieceVocabulary)
    {
        using var doc = JsonDocument.Parse(tokenizerConfigJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("added_tokens_decoder", out var decoder) && decoder.ValueKind == JsonValueKind.Object)
        {
            var contentById = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in decoder.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var id)
                    && property.Value.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    contentById[content.GetString()!] = id;
                }
            }

            return (ResolveFromContentMap(root, contentById,
                "is not in tokenizer_config.json's added_tokens_decoder; its id is never guessed — fix the tokenizer_config.json or hand-write the manifest"), false);
        }

        if (family != TokenizerFamily.SentencePiece)
        {
            throw new ModelDownloadPlanException(
                "tokenizer_config.json has no added_tokens_decoder object; special-token ids are never guessed — cannot pin them");
        }

        if (sentencePieceVocabulary is null)
        {
            // Deferred: the sentencepiece .model file isn't downloaded yet — the caller re-plans
            // with its piece table once the tokenizer file lands on disk (never guessed, D1).
            return (new Dictionary<string, int>(StringComparer.Ordinal), true);
        }

        return (ResolveFromContentMap(root, sentencePieceVocabulary,
            "is not a piece in the sentencepiece model's vocabulary; its id is never guessed — fix tokenizer_config.json or hand-write the manifest"), false);
    }

    private static IReadOnlyDictionary<string, int> ResolveFromContentMap(
        JsonElement root, IReadOnlyDictionary<string, int> contentById, string missingSuffix)
    {
        var specials = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in new[] { "bos_token", "eos_token", "unk_token", "pad_token", "cls_token", "sep_token" })
        {
            if (!root.TryGetProperty(key, out var token) || token.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = token.GetString()!;
            if (!contentById.TryGetValue(name, out var id))
            {
                throw new ModelDownloadPlanException($"special token '{name}' ({key}) {missingSuffix}");
            }

            specials[name] = id;
        }

        return specials;
    }

    private static (PoolingMode Mode, NormalizationMode Normalization, string Provenance) PoolingDecision(
        IReadOnlyDictionary<string, string> rawFiles, OnnxGraphProbe? probe)
    {
        var hasPooledOutput = probe is not null
            && SelectEmbeddingOutput(probe.OutputNames, SelectTokenEmbeddingsOutput(probe.OutputNames)) is not null;

        if (rawFiles.TryGetValue("1_Pooling/config.json", out var poolingJson))
        {
            var mode = PoolingFromFlags(poolingJson);
            if (!rawFiles.TryGetValue("modules.json", out var modulesJson))
            {
                throw new ModelDownloadPlanException(
                    "modules.json is required next to 1_Pooling/config.json to determine normalization (never assumed)");
            }

            var normalization = ModulesDeclareNormalize(modulesJson) ? NormalizationMode.L2 : NormalizationMode.None;

            // #459: the graph's own second output — when it publishes one beside the token-level
            // output — is what the engine actually reads without re-pooling, and outranks the
            // repo's sentence-transformers flags (bge-m3: 1_Pooling says cls, the graph also bakes
            // sentence_embedding; cls happening to reproduce it is luck, not something the planner
            // can rely on for every model).
            if (hasPooledOutput)
            {
                return (PoolingMode.ModelOutput, normalization, "onnx-graph");
            }

            return (mode, normalization, "sentence-transformers");
        }

        // D11 placeholder: no machine-readable pooling provenance — WP5's parity measurement
        // rewrites pooling (and normalization) before the model is trusted.
        var placeholder = hasPooledOutput ? PoolingMode.ModelOutput : PoolingMode.Cls;
        return (placeholder, NormalizationMode.L2, "placeholder(wp5)");
    }

    private static PoolingMode PoolingFromFlags(string poolingJson)
    {
        using var doc = JsonDocument.Parse(poolingJson);
        var root = doc.RootElement;

        if (BoolFlag(root, "pooling_mode_cls_token"))
        {
            return PoolingMode.Cls;
        }

        if (BoolFlag(root, "pooling_mode_mean_tokens") || BoolFlag(root, "pooling_mode_mean_sqrt_len_tokens"))
        {
            return PoolingMode.Mean;
        }

        if (BoolFlag(root, "pooling_mode_lasttoken"))
        {
            return PoolingMode.LastToken;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.StartsWith("pooling_mode_", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.True)
            {
                throw new ModelDownloadPlanException(
                    $"pooling mode '{property.Name}' from 1_Pooling/config.json is not supported (mean, cls and last-token are); hand-write the manifest for this model");
            }
        }

        throw new ModelDownloadPlanException(
            "1_Pooling/config.json declares no pooling_mode_* flag; pooling is never assumed — hand-write the manifest for this model");
    }

    /// <summary>True for the RoBERTa family (incl. XLM-R), whose tokenizers always wrap input.</summary>
    private static bool WrapsWithBosEos(string tokenizerConfigJson)
    {
        using var doc = JsonDocument.Parse(tokenizerConfigJson);
        return doc.RootElement.TryGetProperty("tokenizer_class", out var cls)
               && cls.ValueKind == JsonValueKind.String
               && cls.GetString()?.Contains("Roberta", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     xlm-roberta's vocabulary is the sentencepiece model's pieces shifted by one, behind
    ///     fairseq's own four specials — so an ordinary piece's model id is its sentencepiece id + 1.
    ///     Deliberately narrow: plain RoBERTa is byte-level BPE and never reaches the sentencepiece
    ///     path, and other fairseq ports (CamemBERT) use different offsets and must be hand-written.
    /// </summary>
    /// <remarks>
    ///     Only reached when tokenizer_config.json HAS added_tokens_decoder: the ids are already
    ///     pinned from that decoder, so this offset only shifts the ordinary (non-special) piece ids
    ///     at tokenize time. Without a decoder the offset is measured from data instead — see
    ///     <see cref="ResolveVocabOffset" /> — because the class string alone cannot tell a
    ///     fairseq-offset repo from one that merely uses the XLMRoberta tokenizer class without being
    ///     offset (issue #423's false-refusal regression on faxenoff/code-daemon-embed-v1).
    /// </remarks>
    private static int FairseqVocabOffset(string tokenizerConfigJson)
    {
        using var doc = JsonDocument.Parse(tokenizerConfigJson);
        return doc.RootElement.TryGetProperty("tokenizer_class", out var cls)
               && cls.ValueKind == JsonValueKind.String
               && cls.GetString()?.StartsWith("XLMRoberta", StringComparison.OrdinalIgnoreCase) == true
            ? 1
            : 0;
    }

    private static bool HasAddedTokensDecoder(string tokenizerConfigJson)
    {
        using var doc = JsonDocument.Parse(tokenizerConfigJson);
        return doc.RootElement.TryGetProperty("added_tokens_decoder", out var decoder) && decoder.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    ///     Whether a sentencepiece repo is fairseq-offset is measured from data — config.json's
    ///     vocab_size against the sp model's own piece count, once that piece table is known — never
    ///     the tokenizer_class string alone (issue #423's regression: an XLMRoberta-classed repo can
    ///     still have vocab_size == piece count, i.e. no offset at all; faxenoff/code-daemon-embed-v1
    ///     is exactly that shape and was wrongly refused on the class name).
    /// </summary>
    /// <remarks>
    ///     A repo whose tokenizer_config.json HAS added_tokens_decoder is unaffected: its special-
    ///     token ids already come straight from that decoder, so the offset stays the class-string
    ///     heuristic (<see cref="FairseqVocabOffset" />) that only shifts ordinary piece ids. Before
    ///     the sp model file is downloaded (<paramref name="sentencePieceVocabulary" /> null) the
    ///     piece count is not yet knowable, so the decision is deferred exactly like
    ///     <c>ResolveSpecialTokens</c> defers special-token resolution — never refused on the class
    ///     name alone.
    /// </remarks>
    private static int ResolveVocabOffset(
        string configJson, string tokenizerConfigJson, TokenizerFamily family, bool hasAddedTokensDecoder,
        IReadOnlyDictionary<string, int>? sentencePieceVocabulary)
    {
        if (hasAddedTokensDecoder || family != TokenizerFamily.SentencePiece)
        {
            return FairseqVocabOffset(tokenizerConfigJson);
        }

        if (sentencePieceVocabulary is null)
        {
            return 0;
        }

        var vocabSize = RequiredInt(configJson, "vocab_size", "the fairseq-offset check (measured against the sentencepiece piece table)");
        var pieceCount = sentencePieceVocabulary.Count;
        var difference = vocabSize - pieceCount;
        if (difference == 0)
        {
            return 0;
        }

        throw new ModelDownloadPlanException(
            $"config.json vocab_size ({vocabSize}) and the sentencepiece model's own piece table ({pieceCount} pieces) "
            + $"differ by {difference}, so this is a fairseq-offset tokenizer, and its tokenizer_config.json ships no "
            + "added_tokens_decoder, so the special-token ids can only be guessed. Hand-write ai-raccoon.manifest.json "
            + "for this model with its real special-token ids (docs/how-to/configure-embedding-engines.md, Recipe 4).");
    }

    /// <summary>
    ///     A sentence-transformers module list declares normalization by TYPE. `name` is the module
    ///     index ("2") and `path` the folder ("2_Normalize"), so matching on name misses every real
    ///     repo — bge-m3 included.
    /// </summary>
    private static bool ModulesDeclareNormalize(string modulesJson)
    {
        using var doc = JsonDocument.Parse(modulesJson);
        return doc.RootElement.ValueKind == JsonValueKind.Array
               && doc.RootElement.EnumerateArray().Any(m =>
                   m.ValueKind == JsonValueKind.Object
                   && m.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.GetString()?.EndsWith(".Normalize", StringComparison.Ordinal) == true);
    }

    private static string SelectTokenEmbeddingsOutput(IReadOnlyList<string> outputs)
    {
        foreach (var name in outputs)
        {
            if (name is "token_embeddings" or "last_hidden_state")
            {
                return name;
            }
        }

        // #504: with an unrecognized name and more than one output, never fall back to a name
        // that looks like the graph's own pooled output — that is SelectEmbeddingOutput's role,
        // and picking it here swaps the two outputs (e.g. [sentence_embedding, <tail>]).
        if (outputs.Count > 1)
        {
            var nonPooledLooking = outputs.FirstOrDefault(n =>
                n != "sentence_embedding" && !n.Contains("embedding", StringComparison.OrdinalIgnoreCase));
            if (nonPooledLooking is not null)
            {
                return nonPooledLooking;
            }
        }

        return outputs.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>The graph's pooled-embedding output name, distinct from
    /// <paramref name="tokenEmbeddingsOutput" /> — a sole output serving both roles (the #470/#475
    /// shape) is never returned here, even when it is literally named "sentence_embedding".</summary>
    private static string? SelectEmbeddingOutput(IReadOnlyList<string> outputs, string? tokenEmbeddingsOutput)
    {
        foreach (var name in outputs)
        {
            if (name == tokenEmbeddingsOutput)
            {
                continue;
            }

            if (name == "sentence_embedding" || name.Contains("embedding", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return outputs.Count > 0 && outputs[^1] != tokenEmbeddingsOutput ? outputs[^1] : null;
    }

    private static string ConfigPath(IReadOnlyList<HfTreeEntry> tree, string modelDir, string fileName, string why)
    {
        var besideModel = JoinPath(modelDir, fileName);
        if (TreeEntry(tree, besideModel) is not null)
        {
            return besideModel;
        }

        if (TreeEntry(tree, fileName) is not null)
        {
            return fileName;
        }

        throw new ModelDownloadPlanException(
            $"no '{fileName}' in the repo tree (looked at '{besideModel}' and '{fileName}'); {why}");
    }

    private static string RequireRaw(IReadOnlyDictionary<string, string> rawFiles, string path) =>
        rawFiles.TryGetValue(path, out var content)
            ? content
            : throw new ModelDownloadPlanException($"'{path}' was not fetched; cannot plan the download without it");

    private static string ModelType(string configJson)
    {
        using var doc = JsonDocument.Parse(configJson);
        return doc.RootElement.TryGetProperty("model_type", out var modelType) && modelType.ValueKind == JsonValueKind.String
            ? modelType.GetString()!
            : throw new ModelDownloadPlanException("config.json has no model_type; cannot pair a tokenizer");
    }

    private static int RequiredInt(string configJson, string property, string what)
    {
        using var doc = JsonDocument.Parse(configJson);
        if (doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) && number > 0)
        {
            return number;
        }

        throw new ModelDownloadPlanException($"config.json {property} must be a positive integer (the {what} source)");
    }

    private static bool? ReadBool(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True
            ? true
            : value.ValueKind == JsonValueKind.False ? false : null;
    }

    private static bool BoolFlag(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static HfTreeEntry? TreeEntry(IReadOnlyList<HfTreeEntry> tree, string path) =>
        tree.FirstOrDefault(e => e.Path == path);

    private static PinnedFile Pinned(IReadOnlyList<HfTreeEntry> tree, string path)
    {
        var entry = TreeEntry(tree, path)
            ?? throw new ModelDownloadPlanException($"'{path}' is not in the repo tree");
        return new PinnedFile(path, entry.Size, entry.LfsOid);
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string JoinPath(string dir, string file) =>
        dir.Length == 0 ? file : $"{dir}/{file}";
}
