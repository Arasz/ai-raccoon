using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 provisional D1 manifest adapter (docs/work/2026-08-21-arbitrary-embedding-models-plan.md
///     D1/D5): parses the D1 JSON schema shape into the internal <see cref="EngineDescriptor" /> and
///     enforces the WP3 gates — manifest REQUIRED for directories (M3) and the 384-refusal (M4).
///     PROVISIONAL: lane A (WP1) builds the canonical record + serializer; at the join this adapter
///     is replaced by it (the tests migrate onto the canonical type).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingManifestLoaderTests
{
    private static string WriteModelDir(string manifestJson, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifestJson);
        return dir;
    }

    private static string ShaOf(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static JsonObject BertManifest(int dimensions = 384) => new()
    {
        ["manifestVersion"] = 1,
        ["model"] = "custom-bert",
        ["source"] = new JsonObject { ["repo"] = "org/custom-bert", ["revision"] = "main", ["provider"] = "huggingface" },
        ["provider"] = "local",
        ["dimensions"] = dimensions,
        ["contextWindowTokens"] = 256,
        ["normalization"] = "l2",
        ["tokenizer"] = new JsonObject
        {
            ["family"] = "bert-wordpiece",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = ShaOf("vocab") })
        },
        ["onnx"] = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
            ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
            ["tokenEmbeddingsOutput"] = "last_hidden_state"
        },
        ["pooling"] = new JsonObject { ["mode"] = "mean" }
    };

    [RetryFact]
    public void Load_ParsesTheD1Shape_IntoTheDescriptor()
    {
        var dir = WriteModelDir(BertManifest().ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        var descriptor = new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(dir);

        descriptor.Model.ShouldBe("custom-bert");
        descriptor.SourceRepo.ShouldBe("org/custom-bert");
        descriptor.Dimensions.ShouldBe(384);
        descriptor.ContextWindowTokens.ShouldBe(256);
        descriptor.Normalization.ShouldBe("l2");
        descriptor.Pooling.ShouldBe("mean");
        descriptor.TokenizerFamily.ShouldBe("bert-wordpiece");
        descriptor.TokenizerFile.ShouldBe("vocab.txt");
        descriptor.TokenEmbeddingsOutput.ShouldBe("last_hidden_state");
        descriptor.EmbeddingOutput.ShouldBeNull();
        descriptor.InputNames.ShouldBe(["input_ids", "attention_mask", "token_type_ids"]);
        descriptor.RequiresTokenTypeIds.ShouldBeTrue();
        descriptor.Files.Count.ShouldBe(2);
        descriptor.Files.ShouldContain(f => f.Path == "model.onnx" && f.Sha256 == ShaOf("model"));
    }

    [RetryFact]
    public void Load_SentencePieceManifest_CarriesTheTokenizerOptions()
    {
        var manifest = BertManifest();
        manifest["tokenizer"] = new JsonObject
        {
            ["family"] = "sentencepiece",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "sp.model", ["sha256"] = ShaOf("sp") }),
            ["options"] = new JsonObject
            {
                ["addBeginOfSentence"] = true,
                ["addEndOfSentence"] = true,
                ["specialTokens"] = new JsonObject
                {
                    ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3, ["<mask>"] = 250001
                }
            }
        };

        var descriptor = new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("sp.model", "sp"), ("model.onnx", "model")));

        descriptor.TokenizerFamily.ShouldBe("sentencepiece");
        descriptor.SentencePieceOptions.ShouldNotBeNull();
        descriptor.SentencePieceOptions!.AddBeginOfSentence.ShouldBeTrue();
        descriptor.SentencePieceOptions.AddEndOfSentence.ShouldBeTrue();
        descriptor.SentencePieceOptions.SpecialTokens!["<s>"].ShouldBe(0);
        descriptor.SentencePieceOptions.SpecialTokens["<mask>"].ShouldBe(250001);
        descriptor.SpecialTokenReservation.ShouldBe(2);
    }

    [RetryFact]
    public void Load_DirectoryWithoutManifest_ThrowsActionableError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");

        var ex = Should.Throw<InvalidOperationException>(() => new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(dir));

        ex.Message.ShouldContain("manifest.json", customMessage: "the error must name the missing file");
        ex.Message.ShouldContain("model download", customMessage: "the error must point at the fix");
    }

    [RetryFact]
    public void Load_UnknownTokenizerFamily_ThrowsActionableError()
    {
        var manifest = BertManifest();
        manifest["tokenizer"]!["family"] = "warp-drive";

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"))));

        ex.Message.ShouldContain("warp-drive");
        ex.Message.ShouldContain("bert-wordpiece");
        ex.Message.ShouldContain("sentencepiece");
    }

    [RetryFact]
    public void Load_TokenizerJsonFamily_IsRecognizedButRejectedAsUnsupported()
    {
        var manifest = BertManifest();
        manifest["tokenizer"]!["family"] = "tokenizer-json";

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"))));

        ex.Message.ShouldContain("tokenizer-json");
    }

    [RetryFact]
    public void Load_ModelOutputPoolingWithoutEmbeddingOutput_ThrowsActionableError()
    {
        var manifest = BertManifest();
        manifest["pooling"] = new JsonObject { ["mode"] = "model-output" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"))));

        ex.Message.ShouldContain("model-output");
        ex.Message.ShouldContain("embeddingOutput");
    }

    [RetryFact]
    public void Load_MalformedFileSha_ThrowsActionableError()
    {
        var manifest = BertManifest();
        manifest["onnx"]!["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = "not-a-sha" });

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"))));

        ex.Message.ShouldContain("sha256");
    }

    [RetryFact]
    public void Load_DeclaredFileMissingOnDisk_ThrowsActionableError()
    {
        var manifest = BertManifest();
        var dir = WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab")); // model.onnx absent

        var ex = Should.Throw<InvalidOperationException>(() => new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(dir));

        ex.Message.ShouldContain("model.onnx");
    }

    [RetryFact]
    public void Load_BareStringSpecialTokens_IsRejected_WithNumericMapGuidance()
    {
        var manifest = BertManifest();
        manifest["tokenizer"] = new JsonObject
        {
            ["family"] = "sentencepiece",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "sp.model", ["sha256"] = ShaOf("sp") }),
            ["options"] = new JsonObject { ["addBeginOfSentence"] = true, ["addEndOfSentence"] = true, ["specialTokens"] = "<s>,</s>,<unk>,<pad>,<mask>" }
        };

        var ex = Should.Throw<InvalidOperationException>(() =>
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(WriteModelDir(manifest.ToJsonString(), ("sp.model", "sp"), ("model.onnx", "model"))));

        ex.Message.ShouldContain("numeric");
    }

    /// <summary>
    ///     WP4 lifted the 384-only activation gate: the drain reconciles vec0 to the engine's
    ///     dimension before it writes (D3), so any declared dimension loads.
    /// </summary>
    [RetryFact]
    public void Load_Non384Manifest_IsAccepted()
    {
        var dir = WriteModelDir(BertManifest(dimensions: 1024).ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        var descriptor = new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(dir);

        descriptor.Dimensions.ShouldBe(1024, "the 384-only refusal was WP3's placeholder, removed by WP4");
    }

    /// <summary>
    ///     D1 marks <c>source</c> required and the WP1 validator enforces it; the read path must
    ///     agree with the pinned contract, not accept a manifest the writer could never emit.
    /// </summary>
    [RetryFact]
    public void Load_ManifestWithoutSource_IsRejected()
    {
        var manifest = BertManifest();
        manifest.Remove("source");
        var dir = WriteModelDir(manifest.ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        var ex = Should.Throw<InvalidOperationException>(() => new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(dir));

        ex.Message.ShouldContain("source", customMessage: "the refusal must name the missing required field");
    }
}
