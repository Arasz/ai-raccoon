using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Resolves the bank's embedding engine from settings (FR-NM-3; see docs/work/features-native-memory/native-memory.feature): provider local → the
///     bundled int8 ONNX model in-process; provider openai → any OpenAI-compatible endpoint
///     (baseUrl override). The engine fingerprint is what `model set` persists so a
///     provider/model change triggers a full re-embed.
/// </summary>
public sealed class EmbeddingService
{
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";

    /// <summary>Maximum input tokens of the bundled all-MiniLM-L6-v2 model (see docs/work/2026-08-03-native-memory-plan.md §8).</summary>
    public const int BundledModelContextTokens = 256;

    /// <summary>Documented maximum input of OpenAI-compatible text-embedding models (all share 8191).</summary>
    public const int OpenAiEmbeddingContextTokens = 8191;


    /// <summary>
    ///     Maximum input tokens the configured engine accepts, so chunk sizes can be clamped to
    ///     the model's window (see docs/work/2026-08-03-native-memory-plan.md §8); unknown engines default conservatively.
    /// </summary>
    public static int ContextTokensFor(string provider, string? model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.ToLowerInvariant() switch
        {
            "local" => BundledModelContextTokens,
            "openai" => OpenAiEmbeddingContextTokens,
            _ => BundledModelContextTokens
        };
    }

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

    private IEmbeddingGenerator<string, Embedding<float>> CreateLocal(EmbeddingSettings settings)
    {
        var modelPath = string.IsNullOrWhiteSpace(settings.Model)
            ? BundledModel.ResolveModelPath()
            : Path.GetFullPath(settings.Model);

        // Fail fast on a missing/name-shaped path instead of a cryptic ONNX NoSuchFile error.
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"Configured embedding model '{modelPath}' does not exist (it may be a model name, not a path; ~ is not expanded). " +
                "Run 'ai-raccoon model set local' for the bundled model, or 'ai-raccoon model set local <path-to-onnx>' for a custom path.");
        }

        return new OnnxEmbeddingGenerator(modelPath, BundledModel.ResolveVocabPath());
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAi(EmbeddingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("OpenAI-compatible embeddings require a model id.");
        }

        // The key is a settings row (embedding.apiKey), written by `ai-raccoon model set openai`.
        var apiKey = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI-compatible embeddings require an API key: run 'ai-raccoon model set openai <model> --api-key <key>'.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultOpenAiEndpoint : settings.BaseUrl;
        var client = new EmbeddingClient(settings.Model, new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.AsIEmbeddingGenerator();
    }
}
