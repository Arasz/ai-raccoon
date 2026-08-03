using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
/// Local GGUF embeddings via the official LLamaSharp package (llama.cpp .NET bindings).
/// Model loading is LLamaSharp's (no hand-rolled GGUF parsing); embeddings come from
/// LLamaEmbedder.GetEmbeddings — LLamaSharp 0.27's IEmbeddingGenerator interface method
/// touches a disposed context handle, so this adapter implements the official
/// Microsoft.Extensions.AI abstraction directly over the working GetEmbeddings call.
/// The model path comes from AIRACCOON_TEST_GGUF (same variable as the embedding tests).
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
            PoolingType = LLamaPoolingType.Mean
        };
        weights = LLamaWeights.LoadFromFile(@params);
        var embedder = new LLamaEmbedder(weights, @params, NullLogger.Instance);
        return new LlamaGeneratorAdapter(embedder);
    }

    /// <summary>
    /// Implements IEmbeddingGenerator over LLamaEmbedder.GetEmbeddings. LLamaSharp 0.27's
    /// built-in interface implementation disposes its context handle before use; this
    /// adapter sidesteps that bug while keeping the official abstraction.
    /// </summary>
    private sealed class LlamaGeneratorAdapter(LLamaEmbedder embedder) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                var embeddings = await embedder.GetEmbeddings(value, cancellationToken);
                var vector = embeddings.Single();
                generated.Add(new Embedding<float>(vector));
            }

            return generated;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => embedder.Dispose();
    }
}
