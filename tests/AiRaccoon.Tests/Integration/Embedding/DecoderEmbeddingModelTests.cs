using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     A decoder embedder (Qwen3-Embedding, onnx-community int8 export) runs through
///     <see cref="OnnxEmbeddingGenerator" />: its graph also needs position ids and empty KV-cache
///     inputs, and its vector is the last real token's hidden state. The reference vector came from
///     HF tokenizers + onnxruntime in Python; opt-in because the weights are 600 MB.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class DecoderEmbeddingModelTests
{
    [RetryFact]
    public async Task Qwen3_Int8_EmbedsLikeTheHuggingFaceReference()
    {
        var fixturesDir = Environment.GetEnvironmentVariable(TokenizerJsonParityTests.FixturesDirEnvVar);
        using var reference = JsonDocument.Parse(File.ReadAllText(
            TestData.RepoFile("tests/AiRaccoon.Tests/TestData/Tokenizers/qwen3-int8-reference.json")));
        var root = reference.RootElement;
        var modelDir = string.IsNullOrWhiteSpace(fixturesDir) ? "" : Path.Combine(fixturesDir, root.GetProperty("source").GetString()!);
        var modelPath = Path.Combine(modelDir, root.GetProperty("model").GetString()!);
        if (!File.Exists(modelPath))
        {
            Assert.Skip($"set {TokenizerJsonParityTests.FixturesDirEnvVar} to a directory holding "
                        + "onnx-community_Qwen3-Embedding-0.6B-ONNX/{tokenizer.json,onnx/model_int8.onnx}");
        }

        string[] inputNames;
        using (var probe = new InferenceSession(modelPath))
        {
            inputNames = [.. probe.InputMetadata.Keys];
        }

        var expected = root.GetProperty("vector").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var descriptor = new EngineDescriptor("Qwen/Qwen3-Embedding-0.6B", null, null, expected.Length, 510, "l2", "last-token",
            1, "tokenizer-json", "tokenizer.json", null, false, inputNames, "last_hidden_state", null,
            Path.GetFileName(modelPath), []);
        using var generator = new OnnxEmbeddingGenerator(modelPath,
            TokenizerJsonEmbeddingTokenizer.Create(Path.Combine(modelDir, "tokenizer.json")), descriptor, NullLogger.Instance);

        var embeddings = await generator.GenerateAsync([root.GetProperty("text").GetString()!],
            cancellationToken: TestContext.Current.CancellationToken);

        TestData.Cosine(embeddings.Single().Vector, expected).ShouldBeGreaterThan(0.9999);
    }
}
