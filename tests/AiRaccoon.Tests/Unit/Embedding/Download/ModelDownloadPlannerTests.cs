using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

/// <summary>
///     WP2 (plan §8.2): file auto-selection (onnx/model.onnx preferred, external-data siblings
///     from the ONNX protobuf with glob fallback), tokenizer pairing from config.json model_type,
///     dims/ctx from config.json, numeric special-token ids from tokenizer_config.json (never a
///     guessed mask mapping), pooling provenance per D11 (1_Pooling/modules.json, else a WP5
///     placeholder).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ModelDownloadPlannerTests
{
    private const string OnnxOid = "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b";
    private const string DataOid = "1eebfb28493f67bba03ce0ef64bfdc7fc5a3bd9d7493f818bb1d78cd798416b4";
    private const string SpmOid = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";

    private static readonly HfTreeEntry Onnx = new("onnx/model.onnx", "file", 724_923, OnnxOid);
    private static readonly HfTreeEntry OnnxData = new("onnx/model.onnx_data", "file", 2_266_820_608, DataOid);
    private static readonly HfTreeEntry RootOnnx = new("model.onnx", "file", 724_923, OnnxOid);
    private static readonly HfTreeEntry Spm = new("onnx/sentencepiece.bpe.model", "file", 5_069_051, SpmOid);
    private static readonly HfTreeEntry Vocab = new("vocab.txt", "file", 231_508, null);
    private static readonly HfTreeEntry Config = new("onnx/config.json", "file", 698, null);
    private static readonly HfTreeEntry TokenizerConfig = new("onnx/tokenizer_config.json", "file", 1_173, null);
    private static readonly HfTreeEntry PoolingConfig = new("1_Pooling/config.json", "file", 412, null);
    private static readonly HfTreeEntry Modules = new("modules.json", "file", 512, null);

    private const string XlmRobertaConfig = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 250002}""";

    private const string XlmRobertaTokenizerConfig =
        """
        {
          "model_max_length": 8192,
          "bos_token": "<s>", "eos_token": "</s>", "unk_token": "<unk>", "pad_token": "<pad>",
          "add_bos_token": true, "add_eos_token": true,
          "added_tokens_decoder": {
            "0": { "content": "<s>", "special": true },
            "1": { "content": "<pad>", "special": true },
            "2": { "content": "</s>", "special": true },
            "3": { "content": "<unk>", "special": true },
            "250001": { "content": "<mask>", "special": true }
          }
        }
        """;

    /// <summary>The faxenoff/code-daemon-embed-v1 shape (issue #417): a sentencepiece repo whose
    /// tokenizer_config.json declares the well-known token strings but ships no
    /// added_tokens_decoder object at all.</summary>
    private const string XlmRobertaTokenizerConfigNoDecoder =
        """
        {
          "model_max_length": 8192,
          "bos_token": "<s>", "eos_token": "</s>", "unk_token": "<unk>", "pad_token": "<pad>",
          "add_bos_token": true, "add_eos_token": true
        }
        """;

    private static OnnxGraphProbe BgeM3Probe() => new(
        ExternalDataFiles: ["model.onnx_data"],
        InputNames: ["input_ids", "attention_mask"],
        OutputNames: ["token_embeddings", "sentence_embedding"],
        IrVersion: 6,
        OpsetVersion: 17);

    /// <summary>Probe for ad-hoc trees that omit the external-data sibling.</summary>
    private static ModelDownloadPlanner Planner() => new();

    private static OnnxGraphProbe NoExternalProbe() => BgeM3Probe() with { ExternalDataFiles = [] };

    private static IReadOnlyList<HfTreeEntry> BgeM3Tree() =>
    [
        Onnx, OnnxData, Spm, Config, TokenizerConfig
    ];

    private static Dictionary<string, string> BgeM3Raw(string modelType = "xlm-roberta") =>
        new Dictionary<string, string>
        {
            ["onnx/config.json"] = XlmRobertaConfig.Replace("\"xlm-roberta\"", $"\"{modelType}\""),
            ["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfig
        };

    [Fact]
    public void AutoSelects_OnnxModelOnnx_PreferredOverRootModelOnnx()
    {
        var tree = BgeM3Tree().Append(RootOnnx).ToList();

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe());

        plan.ModelFilePath.ShouldBe("onnx/model.onnx");
    }

    [Fact]
    public void FallsBackTo_RootModelOnnx()
    {
        var tree = new List<HfTreeEntry> { RootOnnx, Vocab, new("config.json", "file", 100, null), new("tokenizer_config.json", "file", 100, null) };

        var plan = Planner().BuildPlan("test/model", "main", tree,
            new Dictionary<string, string>
            {
                ["config.json"] = """{"model_type": "bert", "hidden_size": 384, "max_position_embeddings": 258}""",
                ["tokenizer_config.json"] = BertTokenizerConfig
            }, probe: null);

        plan.ModelFilePath.ShouldBe("model.onnx");
        plan.TokenizerFamily.ShouldBe(TokenizerFamily.BertWordpiece);
        plan.TokenizerFiles.Select(f => f.Path).ShouldBe(["vocab.txt"]);
    }

    [Fact]
    public void NoModelFile_Fails_WithActionableMessage()
    {
        var tree = new List<HfTreeEntry> { Spm };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe()));

        ex.Message.ShouldContain("onnx/model.onnx");
        ex.Message.ShouldContain("model.onnx");
    }

    [Fact]
    public void ExternalData_FromProbe_Appended_AndLfsPinned()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.ModelFiles.Select(f => f.Path).ShouldBe(["onnx/model.onnx", "onnx/model.onnx_data"]);
        plan.ModelFiles.Single(f => f.Path == "onnx/model.onnx_data").LfsSha256.ShouldBe(DataOid);
    }

    [Fact]
    public void ExternalData_GlobFallback_WhenProbeIsNull_DryRun()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe: null);

        plan.ModelFiles.Select(f => f.Path).ShouldBe(["onnx/model.onnx", "onnx/model.onnx_data"]);
        plan.ModelFiles.Single(f => f.Path == "onnx/model.onnx_data").Size.ShouldBe(2_266_820_608);
    }

    [Fact]
    public void ExternalData_DeclaredByProbe_ButMissingFromTree_Fails()
    {
        var tree = BgeM3Tree().Where(e => e.Path != "onnx/model.onnx_data").ToList();

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe()));

        ex.Message.ShouldContain("model.onnx_data");
        ex.Message.ShouldContain("external");
    }

    [Theory]
    [InlineData("bert", "vocab.txt", "bert-wordpiece")]
    [InlineData("xlm-roberta", "sentencepiece.bpe.model", "sentencepiece")]
    [InlineData("roberta", "sentencepiece.bpe.model", "sentencepiece")]
    [InlineData("t5", "spiece.model", "sentencepiece")]
    [InlineData("gpt2", "tokenizer.json", "tokenizer-json")]
    [InlineData("llama", "tokenizer.json", "tokenizer-json")]
    [InlineData("qwen2", "tokenizer.json", "tokenizer-json")]
    public void TokenizerPairing_ByModelType(string modelType, string expectedFile, string expectedFamily)
    {
        var tree = new List<HfTreeEntry>
        {
            Onnx, new("config.json", "file", 100, null), new("tokenizer_config.json", "file", 100, null),
            new(expectedFile, "file", 100, null)
        };
        var raw = new Dictionary<string, string>
        {
            ["config.json"] = XlmRobertaConfig.Replace("\"xlm-roberta\"", $"\"{modelType}\""),
            ["tokenizer_config.json"] = XlmRobertaTokenizerConfig
        };

        if (expectedFamily == "tokenizer-json")
        {
            // D5 gate: tokenizer-json downloads are refused until ML.Tokenizers can consume them.
            var ex = Should.Throw<ModelDownloadPlanException>(() =>
                Planner().BuildPlan("test/model", "main", tree, raw, NoExternalProbe()));
            ex.Message.ShouldContain("tokenizer-json");
            return;
        }

        var plan = Planner().BuildPlan("test/model", "main", tree, raw, NoExternalProbe());
        plan.TokenizerFiles.Select(f => f.Path).ShouldBe([expectedFile]);
        plan.TokenizerFamily.ShouldBe(FamilyOf(expectedFamily));
    }

    [Fact]
    public void UnknownModelType_Fails_WithActionableMessage()
    {
        var tree = new List<HfTreeEntry> { Onnx, Config, TokenizerConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("test/model", "main", tree, BgeM3Raw("gpt-neo-x"), NoExternalProbe()));

        ex.Message.ShouldContain("gpt-neo-x");
        ex.Message.ShouldContain("bert");
    }

    [Fact]
    public void MissingTokenizerFile_Fails_ListingExpectedNames()
    {
        var tree = new List<HfTreeEntry> { Onnx, Config, TokenizerConfig }; // no sentencepiece.bpe.model

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), NoExternalProbe()));

        ex.Message.ShouldContain("sentencepiece.bpe.model");
    }

    [Fact]
    public void MissingConfigJson_Fails_WithActionableMessage()
    {
        var raw = new Dictionary<string, string> { ["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe()));

        ex.Message.ShouldContain("config.json");
    }

    [Fact]
    public void MissingTokenizerConfigJson_Fails_WithActionableMessage()
    {
        var raw = new Dictionary<string, string> { ["onnx/config.json"] = XlmRobertaConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe()));

        ex.Message.ShouldContain("tokenizer_config.json");
    }

    [Fact]
    public void DimsAndContext_FromConfigJson()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.Dimensions.ShouldBe(1024);
        plan.ContextWindowTokens.ShouldBe(8192); // max_position_embeddings 8194 − 2
    }

    [Fact]
    public void SpecialTokenIds_FromAddedTokensDecoder_NeverGuessed()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.SpecialTokens.Count.ShouldBe(4);
        plan.SpecialTokens!["<s>"].ShouldBe(0);
        plan.SpecialTokens!["<pad>"].ShouldBe(1);
        plan.SpecialTokens!["</s>"].ShouldBe(2);
        plan.SpecialTokens!["<unk>"].ShouldBe(3);
        // D1: no <mask> mapping — the mask id is model-specific (250001 for xlm-roberta), never guessed.
        plan.SpecialTokens.ContainsKey("<mask>").ShouldBeFalse();
        plan.AddBeginOfSentence.ShouldBeTrue();
        plan.AddEndOfSentence.ShouldBeTrue();
    }

    [Fact]
    public void DeclaredSpecialToken_MissingFromAddedTokensDecoder_Fails()
    {
        // Rename the <s> entry's CONTENT so the bos_token "<s>" has no id in the decoder.
        var tokenizerConfig = XlmRobertaTokenizerConfig.Replace("\"content\": \"<s>\"", "\"content\": \"<renamed>\"");

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(),
                new Dictionary<string, string>
                {
                    ["onnx/config.json"] = XlmRobertaConfig,
                    ["onnx/tokenizer_config.json"] = tokenizerConfig
                }, BgeM3Probe()));

        ex.Message.ShouldContain("<s>");
        ex.Message.ShouldContain("added_tokens_decoder");
    }

    /// <summary>
    ///     Issue #417 (D1-compatible option 2): a sentencepiece repo whose tokenizer_config.json
    ///     ships no added_tokens_decoder (faxenoff/code-daemon-embed-v1) must NOT refuse the
    ///     download outright — resolution is deferred until the sp model file is on disk and its
    ///     piece table can be consulted (SpecialTokens empty, SpecialTokensPending true).
    /// </summary>
    [Fact]
    public void MissingAddedTokensDecoder_SentencePieceFamily_WithoutVocabulary_DefersInsteadOfThrowing()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfigNoDecoder;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.SpecialTokens.ShouldBeEmpty();
        plan.SpecialTokensPending.ShouldBeTrue();
    }

    /// <summary>The post-download derivation: given the sp model's own piece table (read from disk
    /// by the caller, never inside the pure planner), the same six well-known keys resolve to the
    /// pieces' real ids — a derivation from the model's own data, not a guess (D1). vocab_size is
    /// overridden to match the fake table's own count: the derivation only fires when config.json's
    /// vocab_size and the piece count agree (issue #423 fix — measured from data, not guessed).</summary>
    [Fact]
    public void MissingAddedTokensDecoder_SentencePieceFamily_WithVocabulary_DerivesIds()
    {
        var raw = BgeM3Raw();
        raw["onnx/config.json"] = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 4}""";
        raw["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfigNoDecoder;
        var vocabulary = new Dictionary<string, int> { ["<pad>"] = 0, ["<unk>"] = 1, ["<s>"] = 2, ["</s>"] = 3 };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe(),
            sentencePieceVocabulary: vocabulary);

        plan.SpecialTokensPending.ShouldBeFalse();
        plan.VocabOffset.ShouldBe(0);
        plan.SpecialTokens.Count.ShouldBe(4);
        plan.SpecialTokens["<s>"].ShouldBe(2);
        plan.SpecialTokens["</s>"].ShouldBe(3);
        plan.SpecialTokens["<pad>"].ShouldBe(0);
        plan.SpecialTokens["<unk>"].ShouldBe(1);
    }

    /// <summary>Negative control: D1 still applies — a piece the sp model's vocabulary does not
    /// contain is never guessed, even when the derivation path is exercised. vocab_size matches the
    /// fake table's count so the offset check passes and the missing-piece error is the one that
    /// actually fires.</summary>
    [Fact]
    public void MissingAddedTokensDecoder_SentencePieceFamily_VocabularyMissingPiece_Fails()
    {
        var raw = BgeM3Raw();
        raw["onnx/config.json"] = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 3}""";
        raw["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfigNoDecoder;
        var vocabulary = new Dictionary<string, int> { ["<unk>"] = 1, ["<s>"] = 2, ["</s>"] = 3 }; // no <pad>

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe(),
                sentencePieceVocabulary: vocabulary));

        ex.Message.ShouldContain("<pad>");
        ex.Message.ShouldContain("sentencepiece");
    }

    /// <summary>Wordpiece (and every other non-sentencepiece family) is completely unaffected by
    /// the D1-option-2 fallback: no piece table to derive from, so the original refusal stands.</summary>
    [Fact]
    public void MissingAddedTokensDecoder_NonSentencePieceFamily_StillThrows()
    {
        var tree = new List<HfTreeEntry> { RootOnnx, Vocab, new("config.json", "file", 100, null), new("tokenizer_config.json", "file", 100, null) };
        var tokenizerConfig = BertTokenizerConfig.Replace(
            """
            "added_tokens_decoder": {
                "0": { "content": "[PAD]", "special": true },
                "101": { "content": "[CLS]", "special": true },
                "102": { "content": "[SEP]", "special": true },
                "100": { "content": "[UNK]", "special": true }
              }
            """, "\"model_max_length\": 512");

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("test/model", "main", tree,
                new Dictionary<string, string>
                {
                    ["config.json"] = """{"model_type": "bert", "hidden_size": 384, "max_position_embeddings": 258}""",
                    ["tokenizer_config.json"] = tokenizerConfig
                }, probe: null));

        ex.Message.ShouldContain("added_tokens_decoder");
    }

    /// <summary>
    ///     Issue #459: BAAI/bge-m3 ships 1_Pooling/config.json saying CLS, but its ONNX graph
    ///     ALSO bakes its own pooled output (sentence_embedding) beside the real token-level
    ///     output (token_embeddings). The graph's own vector is what the engine actually reads
    ///     without re-pooling, so it outranks the repo's sentence-transformers flags — CLS
    ///     happening to reproduce the same vector for bge-m3 is luck, not a promise the planner
    ///     can rely on for every model.
    /// </summary>
    [Fact]
    public void Pooling_GraphBakedOutput_OutranksSentenceTransformersFlags()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true, "pooling_mode_mean_tokens": false}""";
        raw["modules.json"] = """[{"idx": 0, "name": "1_Pooling", "path": "", "type": "sentence_transformers.models.Pooling"}, {"idx": 1, "name": "2_Normalize", "path": "", "type": "sentence_transformers.models.Normalize"}]""";

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", tree, raw, BgeM3Probe());

        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.Normalization.ShouldBe(NormalizationMode.L2);
        plan.PoolingProvenance.ShouldBe("onnx-graph");
        plan.EmbeddingOutput.ShouldBe("sentence_embedding");
        plan.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
    }

    /// <summary>Fallback pin: when the graph has no second, distinctly-named pooled output, the
    /// repo's sentence-transformers flags still decide the mode (#459 negative control).</summary>
    [Fact]
    public void Pooling_FromSentenceTransformersLayout_WhenGraphHasNoSecondPooledOutput()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true, "pooling_mode_mean_tokens": false}""";
        raw["modules.json"] = """[{"idx": 0, "name": "1_Pooling", "path": "", "type": "sentence_transformers.models.Pooling"}, {"idx": 1, "name": "2_Normalize", "path": "", "type": "sentence_transformers.models.Normalize"}]""";
        var probe = BgeM3Probe() with { OutputNames = ["token_embeddings"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", tree, raw, probe);

        plan.PoolingMode.ShouldBe(PoolingMode.Cls);
        plan.Normalization.ShouldBe(NormalizationMode.L2);
        plan.PoolingProvenance.ShouldBe("sentence-transformers");
    }

    /// <summary>
    ///     Review fix (#459 PR #496): a SOLE output literally named <c>sentence_embedding</c> is
    ///     the #470/#475 sole-output shape, not a second, distinctly-named pooled output beside a
    ///     real token-level one — <c>SelectTokenEmbeddingsOutput</c>'s fallback and
    ///     <c>SelectEmbeddingOutput</c>'s exact-name match must not collide on the same single
    ///     name. The ST flags still decide here; #475's rank-2 repair (download-time
    ///     <c>PoolingFromGraph</c> / activation-time <c>ManifestPoolingRepair</c>) is what actually
    ///     corrects this shape once the graph's real rank confirms it.
    /// </summary>
    [Fact]
    public void Pooling_SoleOutputNamedSentenceEmbedding_DoesNotOverrideSentenceTransformersFlags()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true, "pooling_mode_mean_tokens": false}""";
        raw["modules.json"] = """[{"idx": 0, "name": "1_Pooling", "path": "", "type": "sentence_transformers.models.Pooling"}, {"idx": 1, "name": "2_Normalize", "path": "", "type": "sentence_transformers.models.Normalize"}]""";
        var probe = BgeM3Probe() with { OutputNames = ["sentence_embedding"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", tree, raw, probe);

        plan.PoolingMode.ShouldBe(PoolingMode.Cls);
        plan.PoolingProvenance.ShouldBe("sentence-transformers");
    }

    /// <summary>
    ///     #504: a two-output graph naming the pooled output FIRST
    ///     (<c>[sentence_embedding, &lt;tail&gt;]</c>) must not let the unrecognized tail steal the
    ///     pooled role — before this fix, <c>SelectTokenEmbeddingsOutput</c>'s naive
    ///     <c>FirstOrDefault</c> fallback picked <c>sentence_embedding</c> itself as the token-level
    ///     output, so the two names came out swapped.
    /// </summary>
    [Fact]
    public void Pooling_TwoOutputs_SentenceEmbeddingNamedFirst_TailIsTheTokenLevelOutput()
    {
        var probe = BgeM3Probe() with { OutputNames = ["sentence_embedding", "hidden_states"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.EmbeddingOutput.ShouldBe("sentence_embedding");
        plan.TokenEmbeddingsOutput.ShouldBe("hidden_states");
        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    /// <summary>
    ///     #504's other named shape — a recognized token-level name plus an unrecognized,
    ///     non-embedding-looking tail (<c>[token_embeddings, &lt;tail&gt;]</c>) — pins the existing
    ///     <c>outputs[^1]</c> fallback as the pooled output. Untested before this fix, not broken.
    /// </summary>
    [Fact]
    public void Pooling_TwoOutputs_TokenEmbeddingsNamedFirst_TailIsThePooledOutput()
    {
        var probe = BgeM3Probe() with { OutputNames = ["token_embeddings", "pooler_output"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
        plan.EmbeddingOutput.ShouldBe("pooler_output");
        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    /// <summary>
    ///     #504 B3: rank is a fact, a name is a guess — with both outputs' real rank known, the
    ///     graph's declared shape decides regardless of which name the list gives first. Both
    ///     shapes named in the issue, in BOTH orders, must land on the same (token, embedding) pair.
    /// </summary>
    [Theory]
    [InlineData("sentence_embedding", "hidden_states")]
    [InlineData("hidden_states", "sentence_embedding")]
    public void Pooling_RankKnown_SelectsByRank_RegardlessOfNameOrder_UnrecognizedNames(string first, string second)
    {
        var probe = BgeM3Probe() with
        {
            OutputNames = [first, second],
            OutputRanks = new Dictionary<string, int> { ["sentence_embedding"] = 2, ["hidden_states"] = 3 }
        };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.TokenEmbeddingsOutput.ShouldBe("hidden_states");
        plan.EmbeddingOutput.ShouldBe("sentence_embedding");
        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    /// <summary>The other named shape (a recognized token-level name plus an unrecognized tail),
    /// same order-independence proof.</summary>
    [Theory]
    [InlineData("token_embeddings", "pooler_output")]
    [InlineData("pooler_output", "token_embeddings")]
    public void Pooling_RankKnown_SelectsByRank_RegardlessOfNameOrder_RecognizedTokenName(string first, string second)
    {
        var probe = BgeM3Probe() with
        {
            OutputNames = [first, second],
            OutputRanks = new Dictionary<string, int> { ["token_embeddings"] = 3, ["pooler_output"] = 2 }
        };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
        plan.EmbeddingOutput.ShouldBe("pooler_output");
        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    /// <summary>
    ///     #504 B4 (round 2 review): "names as tie-breaker" means among candidates that SHARE the
    ///     wanted rank, not "fall back to list order". Three outputs, two of them rank-2
    ///     (<c>pooler_output</c> first in the list, <c>sentence_embedding</c> second) — the
    ///     recognized name must win regardless of which one the graph lists first, never the
    ///     first-found-by-rank.
    /// </summary>
    [Fact]
    public void Pooling_ThreeOutputs_TiedRank_PrefersRecognizedNameOverListOrder()
    {
        var probe = BgeM3Probe() with
        {
            OutputNames = ["last_hidden_state", "pooler_output", "sentence_embedding"],
            OutputRanks = new Dictionary<string, int>
            {
                ["last_hidden_state"] = 3, ["pooler_output"] = 2, ["sentence_embedding"] = 2
            }
        };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.TokenEmbeddingsOutput.ShouldBe("last_hidden_state");
        plan.EmbeddingOutput.ShouldBe("sentence_embedding");
    }

    [Fact]
    public void Pooling_Placeholder_WhenSentenceTransformersLayoutAbsent()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    [Fact]
    public void Placeholder_Cls_WhenGraphHasNoPooledOutput()
    {
        var probe = BgeM3Probe() with { OutputNames = ["token_embeddings"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.PoolingMode.ShouldBe(PoolingMode.Cls);
        plan.EmbeddingOutput.ShouldBeNull();
        plan.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
    }

    [Fact]
    public void UnsupportedPoolingFlag_Fails()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"pooling_mode_weightedmean_tokens": true}""";
        raw["modules.json"] = "[]";

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", tree, raw, BgeM3Probe()));

        ex.Message.ShouldContain("weightedmean");
    }

    [Fact]
    public void RequiresTokenTypeIds_FromGraphInputs()
    {
        var probe = BgeM3Probe() with { InputNames = ["input_ids", "attention_mask", "token_type_ids"] };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.RequiresTokenTypeIds.ShouldBeTrue();
        plan.Inputs.ShouldBe(["input_ids", "attention_mask", "token_type_ids"]);
    }

    [Fact]
    public void NonLfsFiles_AreTofuPinned_WithNullSha()
    {
        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.Files.Single(f => f.Path == "onnx/config.json").LfsSha256.ShouldBeNull();
        plan.Files.Single(f => f.Path == "onnx/model.onnx").LfsSha256.ShouldBe(OnnxOid);
        plan.ProvenanceFiles.Select(f => f.Path).ShouldContain("onnx/config.json");
        plan.ProvenanceFiles.Select(f => f.Path).ShouldContain("onnx/tokenizer_config.json");
    }

    private static TokenizerFamily FamilyOf(string kebab) => kebab switch
    {
        "bert-wordpiece" => TokenizerFamily.BertWordpiece,
        "sentencepiece" => TokenizerFamily.SentencePiece,
        _ => TokenizerFamily.TokenizerJson
    };

    private const string BertTokenizerConfig =
        """
        {
          "bos_token": "[CLS]", "eos_token": "[SEP]", "unk_token": "[UNK]", "pad_token": "[PAD]",
          "added_tokens_decoder": {
            "0": { "content": "[PAD]", "special": true },
            "101": { "content": "[CLS]", "special": true },
            "102": { "content": "[SEP]", "special": true },
            "100": { "content": "[UNK]", "special": true }
          }
        }
        """;

    /// <summary>
    ///     Regression from the real WP5 download: bge-m3's modules.json names the Normalize module
    ///     `"name": "2"` with `"path": "2_Normalize"` — the name is an index, not the folder. Matching
    ///     on name reported "no Normalize" and wrote `normalization: none` for a model that is
    ///     L2-normalized. The fixture that let this through matched the code, not Hugging Face.
    /// </summary>
    [Fact]
    public void Normalization_FromRealModulesJsonShape_IsL2()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true}""";
        raw["modules.json"] = """
            [{"idx": 0, "name": "0", "path": "", "type": "sentence_transformers.models.Transformer"},
             {"idx": 1, "name": "1", "path": "1_Pooling", "type": "sentence_transformers.models.Pooling"},
             {"idx": 2, "name": "2", "path": "2_Normalize", "type": "sentence_transformers.models.Normalize"}]
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", tree, raw, BgeM3Probe());

        plan.Normalization.ShouldBe(NormalizationMode.L2,
            "modules.json declares a Normalize module; match on its type, not the folder-shaped name");
    }

    /// <summary>
    ///     Also from the real download: bge-m3's tokenizer_config.json declares NEITHER
    ///     add_bos_token NOR add_eos_token — HF puts that behaviour in the tokenizer class. Defaulting
    ///     to false made every embedding skip the `&lt;s&gt;`/`&lt;/s&gt;` wrapper the model was trained
    ///     with, which is a silent quality regression, not a crash.
    /// </summary>
    [Fact]
    public void BosEos_AbsentFromAnXlmRobertaConfig_DefaultToTrue()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "XLMRobertaTokenizer", "bos_token": "<s>", "eos_token": "</s>",
             "unk_token": "<unk>", "pad_token": "<pad>",
             "added_tokens_decoder": {"0": {"content": "<s>"}, "1": {"content": "<pad>"},
                                      "2": {"content": "</s>"}, "3": {"content": "<unk>"}}}
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.AddBeginOfSentence.ShouldBeTrue("XLM-R always wraps input in <s> … </s>");
        plan.AddEndOfSentence.ShouldBeTrue();
    }

    /// <summary>
    ///     xlm-roberta numbers its vocabulary as the sentencepiece pieces shifted behind fairseq's
    ///     four specials, so an ordinary piece's model id is its sentencepiece id + 1. Without the
    ///     offset the model reads a neighbouring row for every token.
    /// </summary>
    [Fact]
    public void VocabOffset_ForAnXlmRobertaConfig_IsTheFairseqOffset()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "XLMRobertaTokenizer", "bos_token": "<s>", "eos_token": "</s>",
             "unk_token": "<unk>", "pad_token": "<pad>",
             "added_tokens_decoder": {"0": {"content": "<s>"}, "1": {"content": "<pad>"},
                                      "2": {"content": "</s>"}, "3": {"content": "<unk>"}}}
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.VocabOffset.ShouldBe(1);
    }

    /// <summary>
    ///     #423 regression (false refusal on faxenoff/code-daemon-embed-v1): whether a decoder-less
    ///     sentencepiece repo is fairseq-offset can only be measured once the sp model's own piece
    ///     table is known — the tokenizer_class string alone is not enough (an XLMRoberta-classed
    ///     repo can still have vocab_size == piece count, i.e. no offset at all). Before the sp file
    ///     is downloaded there is no piece table to measure against, so the plan must defer exactly
    ///     like any other no-decoder sentencepiece repo — never refuse on the class name alone.
    /// </summary>
    [Fact]
    public void MissingAddedTokensDecoder_XlmRobertaClass_WithoutVocabulary_DefersInsteadOfThrowing()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "XLMRobertaTokenizer", "bos_token": "<s>", "eos_token": "</s>",
             "unk_token": "<unk>", "pad_token": "<pad>"}
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.SpecialTokens.ShouldBeEmpty();
        plan.SpecialTokensPending.ShouldBeTrue();
    }

    /// <summary>
    ///     The other half of the #423 fix: an XLMRoberta-classed repo whose vocab_size DOES equal
    ///     the piece count (faxenoff/code-daemon-embed-v1: 22739 == 22739, verified against the real
    ///     repo) is NOT fairseq-offset and must derive ids from the piece table like any other
    ///     sentencepiece repo with no added_tokens_decoder — the class string must never override
    ///     what the data says.
    /// </summary>
    [Fact]
    public void MissingAddedTokensDecoder_XlmRobertaClass_VocabSizeMatchesPieces_DerivesIds_NotRefused()
    {
        var raw = BgeM3Raw();
        raw["onnx/config.json"] = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 4}""";
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "XLMRobertaTokenizer", "bos_token": "<s>", "eos_token": "</s>",
             "unk_token": "<unk>", "pad_token": "<pad>"}
            """;
        var vocabulary = new Dictionary<string, int> { ["<pad>"] = 0, ["<unk>"] = 1, ["<s>"] = 2, ["</s>"] = 3 };

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe(),
            sentencePieceVocabulary: vocabulary);

        plan.SpecialTokensPending.ShouldBeFalse();
        plan.VocabOffset.ShouldBe(0);
        plan.SpecialTokens["<s>"].ShouldBe(2);
        plan.SpecialTokens["<unk>"].ShouldBe(1);
    }

    /// <summary>
    ///     The two #417 and #416 mechanisms must not silently combine. Special-token ids derived from
    ///     the sp model's piece table are in SENTENCEPIECE numbering (&lt;unk&gt;=0, &lt;s&gt;=1); a
    ///     fairseq-offset model needs them in ITS numbering (&lt;s&gt;=0, &lt;unk&gt;=3). Writing the
    ///     former into a manifest that also carries a nonzero offset embeds the wrong bos and unk for
    ///     every sequence — the same silent-wrong-embeddings failure the offset exists to fix, so it
    ///     is refused rather than guessed at from the fairseq convention. vocab_size 6 vs. a 4-piece
    ///     table pins the real bge-m3 shape (vocab_size 250002 vs. 250000 pieces, difference 2 —
    ///     verified against the real repo) at a size the test can construct by hand.
    /// </summary>
    [Fact]
    public void XlmRobertaRepoWithoutAddedTokensDecoder_VocabSizeExceedsPieces_IsRefused_CitingMeasuredDifference()
    {
        var raw = BgeM3Raw();
        raw["onnx/config.json"] = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 6}""";
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "XLMRobertaTokenizer", "bos_token": "<s>", "eos_token": "</s>",
             "unk_token": "<unk>", "pad_token": "<pad>"}
            """;
        var vocabulary = new Dictionary<string, int> { ["<unk>"] = 0, ["<s>"] = 1, ["</s>"] = 2, ["x"] = 3 };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe(),
                sentencePieceVocabulary: vocabulary));

        ex.Message.ShouldContain("added_tokens_decoder");
        ex.Message.ShouldContain("6", customMessage: "must name the measured vocab_size, not the class name");
        ex.Message.ShouldContain("4", customMessage: "must name the measured piece count");
        ex.Message.ShouldNotContain("'xlm-roberta'",
            customMessage: "#423 refused by quoting the model_type; the fix must cite the measured difference instead");
    }

    /// <summary>A plain sentencepiece model numbers its own pieces; shifting them would break it.</summary>
    [Fact]
    public void VocabOffset_ForANonFairseqConfig_IsZero()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "T5Tokenizer", "unk_token": "<unk>", "pad_token": "<pad>",
             "added_tokens_decoder": {"0": {"content": "<pad>"}, "1": {"content": "<unk>"}}}
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.VocabOffset.ShouldBe(0);
    }

    [Fact]
    public void BosEos_AbsentFromANonRobertaConfig_StayFalse()
    {
        var raw = BgeM3Raw();
        raw["onnx/tokenizer_config.json"] = """
            {"tokenizer_class": "T5Tokenizer", "unk_token": "<unk>", "pad_token": "<pad>",
             "added_tokens_decoder": {"0": {"content": "<pad>"}, "1": {"content": "<unk>"}}}
            """;

        var plan = Planner().BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe());

        plan.AddBeginOfSentence.ShouldBeFalse("only the Roberta family has the implicit wrapper");
    }
}
