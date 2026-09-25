using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     A manifest's queryInstruction and documentInstruction are the prompts a model was trained
///     with (e.g. EmbeddingGemma's "task: search result | query: "): queries and stored text are
///     embedded with them, and editing either re-embeds because the fingerprint hashes the manifest.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingServiceInstructionTests
{
    private const string QueryPrompt = "task: search result | query: ";
    private const string DocumentPrompt = "title: none | text: ";

    [RetryFact]
    public void TrimQueryToWindow_PrependsTheManifestsQueryInstruction()
    {
        var settings = ManifestSettings(QueryPrompt, DocumentPrompt);

        Service().TrimQueryToWindow(settings, "hybrid search").ShouldBe(QueryPrompt + "hybrid search");
    }

    [RetryFact]
    public void DocumentText_PrependsTheManifestsDocumentInstruction()
    {
        var settings = ManifestSettings(QueryPrompt, DocumentPrompt);

        Service().DocumentText(settings, "stored note").ShouldBe(DocumentPrompt + "stored note");
    }

    [RetryFact]
    public void WithoutInstructions_QueryAndDocumentTextAreUnchanged()
    {
        var settings = ManifestSettings(null, null);

        Service().TrimQueryToWindow(settings, "hybrid search").ShouldBe("hybrid search");
        Service().DocumentText(settings, "stored note").ShouldBe("stored note");
    }

    [RetryFact]
    public void BundledEngine_DocumentTextIsUnchanged()
    {
        Service().DocumentText(new EmbeddingSettings("local", null, null, null), "stored note").ShouldBe("stored note");
    }

    [RetryFact]
    public void EditingTheDocumentInstruction_ChangesTheEngineFingerprint()
    {
        var dir = ManifestSettings(QueryPrompt, DocumentPrompt).Model!;
        var service = Service();
        var before = service.EngineFingerprint("local", dir, null);

        var manifestPath = Path.Combine(dir, EmbeddingManifest.FileName);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["documentInstruction"] = "passage: ";
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        service.EngineFingerprint("local", dir, null).ShouldNotBe(before, "a changed prompt must re-embed the bank");
    }

    private static EmbeddingService Service() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System, TestData.EmbeddingOptions());

    private static EmbeddingSettings ManifestSettings(string? queryInstruction, string? documentInstruction)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-instruction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vocab = File.ReadAllBytes(BundledModel.ResolveVocabPath());
        File.WriteAllBytes(Path.Combine(dir, "vocab.txt"), vocab);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["model"] = "instruction-test-model",
            ["source"] = new JsonObject { ["repo"] = "test/instruction-test-model", ["revision"] = "main" },
            ["provider"] = "local",
            ["dimensions"] = 384,
            ["contextWindowTokens"] = 512,
            ["normalization"] = "l2",
            ["queryInstruction"] = queryInstruction,
            ["documentInstruction"] = documentInstruction,
            ["tokenizer"] = new JsonObject
            {
                ["family"] = "bert-wordpiece",
                ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = Sha(vocab) })
            },
            ["onnx"] = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = Sha("model"u8.ToArray()) }),
                ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
                ["tokenEmbeddingsOutput"] = "last_hidden_state"
            },
            ["pooling"] = new JsonObject { ["mode"] = "mean" }
        };
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return new EmbeddingSettings("local", dir, null, null);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
