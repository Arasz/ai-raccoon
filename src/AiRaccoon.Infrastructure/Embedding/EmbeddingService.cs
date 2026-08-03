using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Resolves the bank's embedding engine from settings (FR-NM-3): provider local → the
///     bundled int8 ONNX model in-process; provider openai → any OpenAI-compatible endpoint
///     (baseUrl override). The engine fingerprint is what memory_configure persists so a
///     provider/model change triggers a full re-embed.
/// </summary>
public sealed class EmbeddingService
{
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";

    public const string OpenAiApiKeyEnvVar = "AIRACCOON_OPENAI_API_KEY";

    // The service owns generator lifetimes: an ONNX session (23 MB model) and an OpenAI client
    // are expensive to build, so engines are cached per fingerprint and never disposed by callers.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _engines =
        new(StringComparer.Ordinal);

    /// <summary>Stable engine identity recorded in settings; a change re-embeds the bank.</summary>
    public static string EngineFingerprint(string provider, string? model, string? baseUrl) =>
        provider.ToLowerInvariant() switch
        {
            "local" => $"local:{(string.IsNullOrWhiteSpace(model) ? "bundled" : model)}",
            "openai" =>
                $"openai:{model}@{(string.IsNullOrWhiteSpace(baseUrl) ? DefaultOpenAiEndpoint : baseUrl)}",
            var other => other
        };

    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = settings.Provider.ToLowerInvariant();
        return provider switch
        {
            "local" or "openai" => _engines.GetOrAdd(EngineFingerprint(provider, settings.Model, settings.BaseUrl),
                _ => provider == "local" ? CreateLocal(settings) : CreateOpenAi(settings)),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Provider,
                "Unknown embedding provider; expected 'local' or 'openai'.")
        };
    }

    private static OnnxEmbeddingGenerator CreateLocal(EmbeddingSettings settings)
    {
        var modelPath = string.IsNullOrWhiteSpace(settings.Model)
            ? BundledModel.ResolveModelPath()
            : Path.GetFullPath(settings.Model);
        return new OnnxEmbeddingGenerator(modelPath, BundledModel.ResolveVocabPath());
    }

    // Note: the api key resolves at generator creation (arg wins, then env); swapping the
    // AIRACCOON_OPENAI_API_KEY env after first use requires a configure call to re-resolve.
    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAi(EmbeddingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("OpenAI-compatible embeddings require a model id.");
        }

        // The key is never persisted; the tool's api_key arg wins, else the env fallback.
        var apiKey = settings.ApiKey ?? Environment.GetEnvironmentVariable(OpenAiApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible embeddings require an API key: pass api_key or set {OpenAiApiKeyEnvVar}.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultOpenAiEndpoint : settings.BaseUrl;
        var client = new EmbeddingClient(settings.Model, new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.AsIEmbeddingGenerator();
    }
}
