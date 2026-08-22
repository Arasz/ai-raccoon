using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     D1: activation re-verifies every manifest-pinned file's sha256 against the bytes on disk,
///     not just its presence — a swapped <c>model.onnx</c> with an untouched manifest must refuse
///     activation instead of silently embedding through a different model. New file (not
///     <see cref="EmbeddingManifestLoaderTests" />): #405 rewrites that one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingManifestPinVerificationTests
{
    private static string WriteModelDir(string manifestJson, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-pin-verification-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifestJson);
        return dir;
    }

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static JsonObject BertManifest() => new()
    {
        ["manifestVersion"] = 1,
        ["model"] = "custom-bert",
        ["source"] = new JsonObject { ["repo"] = "org/custom-bert", ["revision"] = "main" },
        ["provider"] = "local",
        ["dimensions"] = 384,
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

    private static EmbeddingManifestLoader Loader() =>
        new(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator());

    [Fact]
    public void ASwappedPinnedFile_RefusesActivation()
    {
        var dir = WriteModelDir(BertManifest().ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"));
        // The manifest still pins the ORIGINAL bytes' sha256; the file on disk is swapped in place.
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "a-different-model-entirely");

        var ex = Should.Throw<InvalidOperationException>(() => Loader().Load(dir));

        ex.Message.ShouldContain("model.onnx", customMessage: "the refusal must name the offending file");
        ex.Message.ShouldContain(ShaOf("model"), customMessage: "the refusal must name the expected digest");
        ex.Message.ShouldContain(ShaOf("a-different-model-entirely"), customMessage: "the refusal must name the actual digest");
    }

    [Fact]
    public void AnIntactModelDirectory_StillActivates()
    {
        var dir = WriteModelDir(BertManifest().ToJsonString(), ("vocab.txt", "vocab"), ("model.onnx", "model"));

        var descriptor = Loader().Load(dir);

        descriptor.Model.ShouldBe("custom-bert");
    }

    [Fact]
    public void AMissingPinnedFile_StillFailsWithTheOldMessage()
    {
        var dir = WriteModelDir(BertManifest().ToJsonString(), ("vocab.txt", "vocab")); // model.onnx absent

        var ex = Should.Throw<InvalidOperationException>(() => Loader().Load(dir));

        ex.Message.ShouldContain("model.onnx");
        ex.Message.ShouldContain("missing from", customMessage: "the missing-file message must stay distinct from the sha-mismatch message");
        ex.Message.ShouldContain("model download");
    }
}
