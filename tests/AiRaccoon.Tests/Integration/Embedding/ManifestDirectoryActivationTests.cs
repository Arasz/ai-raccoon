using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 directory activation (M3/M4): a directory model REQUIRES a manifest.json; only the
///     legacy .onnx-file path keeps the bundled defaults. Non-384 manifests are refused with an
///     actionable error until the dimension-reconcile work (WP4) lands.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ManifestDirectoryActivationTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string ShaOfFile(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static EmbeddingService Service() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));

    // D1: the manifest now pins real bytes, not placeholder content — pass the sha256 of what is
    // actually on disk at each path.
    private static JsonObject Manifest(int dimensions = 384, string? vocabSha = null, string? onnxSha = null) => new()
    {
        ["manifestVersion"] = 1,
        ["model"] = "activation-test-model",
        ["source"] = new JsonObject { ["repo"] = "test/activation-test-model", ["revision"] = "main" },
        ["provider"] = "local",
        ["dimensions"] = dimensions,
        ["contextWindowTokens"] = 256,
        ["normalization"] = "l2",
        ["tokenizer"] = new JsonObject
        {
            ["family"] = "bert-wordpiece",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = vocabSha ?? ShaOf("vocab") })
        },
        ["onnx"] = new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = onnxSha ?? ShaOf("model") }),
            ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
            ["tokenEmbeddingsOutput"] = "last_hidden_state"
        },
        ["pooling"] = new JsonObject { ["mode"] = "mean" }
    };

    [Fact]
    public void CreateGenerator_DirectoryWithoutManifest_ThrowsActionableError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-activation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");

        var ex = Should.Throw<InvalidOperationException>(() =>
            Service().CreateGenerator(new EmbeddingSettings("local", dir, null, null)));

        ex.Message.ShouldContain("manifest.json", customMessage: "the refusal must name the missing manifest");
        ex.Message.ShouldContain("model download", customMessage: "the refusal must point at the fix");
    }

    [Fact]
    public async Task CreateGenerator_Valid384ManifestDirectory_BuildsAndEmbeds()
    {
        // The manifest points at copies of the REAL bundled model + vocab, so the whole manifest
        // path — loader, tokenizer factory, generator — runs end-to-end on known-good artifacts.
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-activation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vocabPath = Path.Combine(dir, "vocab.txt");
        var onnxPath = Path.Combine(dir, "model.onnx");
        File.Copy(BundledModel.ResolveVocabPath(), vocabPath);
        File.Copy(BundledModel.ResolveModelPath(), onnxPath);
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName),
            Manifest(vocabSha: ShaOfFile(vocabPath), onnxSha: ShaOfFile(onnxPath)).ToJsonString());

        using var generator = Service().CreateGenerator(new EmbeddingSettings("local", dir, null, null));

        var result = await generator.GenerateAsync(["Memory retrieval ranking evidence"],
            cancellationToken: TestContext.Current.CancellationToken);
        var vector = result[0].Vector.ToArray();
        vector.Length.ShouldBe(384);
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        norm.ShouldBe(1.0, 1e-4, "the manifest path must apply the same mean-pool + L2 semantics");
    }
}
