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

    private static OnnxGraphProbe BgeM3Probe() => new(
        ExternalDataFiles: ["model.onnx_data"],
        InputNames: ["input_ids", "attention_mask"],
        OutputNames: ["token_embeddings", "sentence_embedding"],
        IrVersion: 6,
        OpsetVersion: 17);

    /// <summary>Probe for ad-hoc trees that omit the external-data sibling.</summary>
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

        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe());

        plan.ModelFilePath.ShouldBe("onnx/model.onnx");
    }

    [Fact]
    public void FallsBackTo_RootModelOnnx()
    {
        var tree = new List<HfTreeEntry> { RootOnnx, Vocab, new("config.json", "file", 100, null), new("tokenizer_config.json", "file", 100, null) };

        var plan = ModelDownloadPlanner.BuildPlan("test/model", "main", tree,
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
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe()));

        ex.Message.ShouldContain("onnx/model.onnx");
        ex.Message.ShouldContain("model.onnx");
    }

    [Fact]
    public void ExternalData_FromProbe_Appended_AndLfsPinned()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.ModelFiles.Select(f => f.Path).ShouldBe(["onnx/model.onnx", "onnx/model.onnx_data"]);
        plan.ModelFiles.Single(f => f.Path == "onnx/model.onnx_data").LfsSha256.ShouldBe(DataOid);
    }

    [Fact]
    public void ExternalData_GlobFallback_WhenProbeIsNull_DryRun()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe: null);

        plan.ModelFiles.Select(f => f.Path).ShouldBe(["onnx/model.onnx", "onnx/model.onnx_data"]);
        plan.ModelFiles.Single(f => f.Path == "onnx/model.onnx_data").Size.ShouldBe(2_266_820_608);
    }

    [Fact]
    public void ExternalData_DeclaredByProbe_ButMissingFromTree_Fails()
    {
        var tree = BgeM3Tree().Where(e => e.Path != "onnx/model.onnx_data").ToList();

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), BgeM3Probe()));

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
                ModelDownloadPlanner.BuildPlan("test/model", "main", tree, raw, NoExternalProbe()));
            ex.Message.ShouldContain("tokenizer-json");
            return;
        }

        var plan = ModelDownloadPlanner.BuildPlan("test/model", "main", tree, raw, NoExternalProbe());
        plan.TokenizerFiles.Select(f => f.Path).ShouldBe([expectedFile]);
        plan.TokenizerFamily.ShouldBe(FamilyOf(expectedFamily));
    }

    [Fact]
    public void UnknownModelType_Fails_WithActionableMessage()
    {
        var tree = new List<HfTreeEntry> { Onnx, Config, TokenizerConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            ModelDownloadPlanner.BuildPlan("test/model", "main", tree, BgeM3Raw("gpt-neo-x"), NoExternalProbe()));

        ex.Message.ShouldContain("gpt-neo-x");
        ex.Message.ShouldContain("bert");
    }

    [Fact]
    public void MissingTokenizerFile_Fails_ListingExpectedNames()
    {
        var tree = new List<HfTreeEntry> { Onnx, Config, TokenizerConfig }; // no sentencepiece.bpe.model

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, BgeM3Raw(), NoExternalProbe()));

        ex.Message.ShouldContain("sentencepiece.bpe.model");
    }

    [Fact]
    public void MissingConfigJson_Fails_WithActionableMessage()
    {
        var raw = new Dictionary<string, string> { ["onnx/tokenizer_config.json"] = XlmRobertaTokenizerConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe()));

        ex.Message.ShouldContain("config.json");
    }

    [Fact]
    public void MissingTokenizerConfigJson_Fails_WithActionableMessage()
    {
        var raw = new Dictionary<string, string> { ["onnx/config.json"] = XlmRobertaConfig };

        var ex = Should.Throw<ModelDownloadPlanException>(() =>
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), raw, BgeM3Probe()));

        ex.Message.ShouldContain("tokenizer_config.json");
    }

    [Fact]
    public void DimsAndContext_FromConfigJson()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.Dimensions.ShouldBe(1024);
        plan.ContextWindowTokens.ShouldBe(8192); // max_position_embeddings 8194 − 2
    }

    [Fact]
    public void SpecialTokenIds_FromAddedTokensDecoder_NeverGuessed()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.SpecialTokens.Count.ShouldBe(4);
        plan.SpecialTokens["<s>"].ShouldBe(0);
        plan.SpecialTokens["<pad>"].ShouldBe(1);
        plan.SpecialTokens["</s>"].ShouldBe(2);
        plan.SpecialTokens["<unk>"].ShouldBe(3);
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
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(),
                new Dictionary<string, string>
                {
                    ["onnx/config.json"] = XlmRobertaConfig,
                    ["onnx/tokenizer_config.json"] = tokenizerConfig
                }, BgeM3Probe()));

        ex.Message.ShouldContain("<s>");
        ex.Message.ShouldContain("added_tokens_decoder");
    }

    [Fact]
    public void Pooling_FromSentenceTransformersLayout()
    {
        var tree = BgeM3Tree().Append(PoolingConfig).Append(Modules).ToList();
        var raw = BgeM3Raw();
        raw["1_Pooling/config.json"] = """{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true, "pooling_mode_mean_tokens": false}""";
        raw["modules.json"] = """[{"idx": 0, "name": "1_Pooling", "path": "", "type": "sentence_transformers.models.Pooling"}, {"idx": 1, "name": "2_Normalize", "path": "", "type": "sentence_transformers.models.Normalize"}]""";

        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, raw, BgeM3Probe());

        plan.PoolingMode.ShouldBe(PoolingMode.Cls);
        plan.Normalization.ShouldBe(NormalizationMode.L2);
        plan.PoolingProvenance.ShouldBe("sentence-transformers");
    }

    [Fact]
    public void Pooling_Placeholder_WhenSentenceTransformersLayoutAbsent()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

        plan.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        plan.PoolingProvenance.ShouldContain("placeholder");
    }

    [Fact]
    public void Placeholder_Cls_WhenGraphHasNoPooledOutput()
    {
        var probe = BgeM3Probe() with { OutputNames = ["token_embeddings"] };

        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

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
            ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", tree, raw, BgeM3Probe()));

        ex.Message.ShouldContain("weightedmean");
    }

    [Fact]
    public void RequiresTokenTypeIds_FromGraphInputs()
    {
        var probe = BgeM3Probe() with { InputNames = ["input_ids", "attention_mask", "token_type_ids"] };

        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), probe);

        plan.RequiresTokenTypeIds.ShouldBeTrue();
        plan.Inputs.ShouldBe(["input_ids", "attention_mask", "token_type_ids"]);
    }

    [Fact]
    public void NonLfsFiles_AreTofuPinned_WithNullSha()
    {
        var plan = ModelDownloadPlanner.BuildPlan("BAAI/bge-m3", "main", BgeM3Tree(), BgeM3Raw(), BgeM3Probe());

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
}
