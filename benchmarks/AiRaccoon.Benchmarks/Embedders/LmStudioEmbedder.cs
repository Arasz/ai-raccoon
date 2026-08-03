using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
///     LM Studio embeddings via the official OpenAI SDK (Microsoft.Extensions.AI.OpenAI
///     AsIEmbeddingGenerator). LM Studio exposes an OpenAI-compatible /v1 endpoint, so the
///     official client works against it with a placeholder key — no hand-rolled HTTP code.
/// </summary>
public sealed class LmStudioEmbedder(string model, string? baseUrl = null) : EmbeddingBackend(CreateGenerator(model, baseUrl))
{
    private readonly string _baseUrl = baseUrl ?? Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL") ?? "http://localhost:1234";

    protected override string BackendName => $"lmstudio:{model}";

    private static IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(string model, string? baseUrl)
    {
        var url = baseUrl ?? Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL") ?? "http://localhost:1234";
        var client = new OpenAIClient(
            new ApiKeyCredential("lm-studio"), // LM Studio does not validate the key
            new OpenAIClientOptions { Endpoint = new Uri(url.TrimEnd('/') + "/v1") });
        return client.GetEmbeddingClient(model).AsIEmbeddingGenerator();
    }
}
