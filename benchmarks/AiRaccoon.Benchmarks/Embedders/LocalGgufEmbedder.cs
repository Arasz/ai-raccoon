using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
/// Local GGUF embeddings via the official LLamaSharp package (llama.cpp .NET bindings).
/// The model path comes from AIRACCOON_TEST_GGUF — the same variable that gates the
/// embedding integration tests. No hand-rolled GGUF parsing: LLamaSharp loads the model
/// and reports the true embedding dimension.
/// </summary>
public sealed class LocalGgufEmbedder : EmbeddingBackend
{
    private readonly LLamaWeights _weights;

    public LocalGgufEmbedder(string? modelPath = null)
        : base(CreateGenerator(modelPath, out var weights))
    {
        _weights = weights;
    }

    protected override string BackendName => $"local:{Path.GetFileName(ModelPath)}";

    private static string ModelPath { get; } =
        Environment.GetEnvironmentVariable("AIRACCOON_TEST_GGUF")
        ?? throw new InvalidOperationException(
            "AIRACCOON_TEST_GGUF is not set; pass the GGUF path or run scripts/download-embedding-model.sh first.");

    public override void Dispose()
    {
        base.Dispose();
        _weights.Dispose();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(
        string? modelPath, out LLamaWeights weights)
    {
        var path = modelPath ?? ModelPath;
        var @params = new ModelParams(path)
        {
            PoolingType = LLamaPoolingType.Mean,
        };
        weights = LLamaWeights.LoadFromFile(@params);
        return new LLamaEmbedder(weights, @params, NullLogger.Instance);
    }
}
