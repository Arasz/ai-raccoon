using AiRaccoon.Core.Chunking;
using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Resolves the bank's embedding engine from settings (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): local → the bundled int8 ONNX model
///     in-process, openai → any OpenAI-compatible endpoint. A fingerprint change triggers a full re-embed.
/// </summary>
public sealed partial class EmbeddingService(ILogger<EmbeddingService> logger) : IEmbeddingService
{
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";

    /// <summary>
    ///     Maximum input tokens of the bundled all-MiniLM-L6-v2 model (see docs/work/archive/2026-08-03-native-memory-plan.md
    ///     §8).
    /// </summary>
    public const int BundledModelContextTokens = 256;

    /// <summary>Documented maximum input of OpenAI-compatible text-embedding models (all share 8191).</summary>
    public const int OpenAiEmbeddingContextTokens = 8191;

    private readonly ILogger<EmbeddingService> _logger = logger;

    // The service owns generator lifetimes: an ONNX session (23 MB model) and an OpenAI client
    // are expensive to build, so engines are cached per fingerprint and never disposed by callers.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _engines =
        new(StringComparer.Ordinal);

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


    /// <summary>
    ///     Maximum input tokens the configured engine accepts, so chunk sizes can be clamped to
    ///     the model's window (see docs/work/archive/2026-08-03-native-memory-plan.md §8); unknown engines default conservatively.
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

    /// <summary>
    ///     Content-token chunk budget for a provider, counted in that provider's own tokenizer
    ///     (docs/adr/0036): "local" reserves 2 of the model's 256-token window for the BERT
    ///     [CLS]/[SEP] special tokens <see cref="OnnxEmbeddingGenerator" /> adds at embed time, so
    ///     the chunker must never emit more than <see cref="OnnxEmbeddingGenerator.MaxContentTokens" />
    ///     real content tokens. Other providers' context window (<see cref="ContextTokensFor" />) is
    ///     the budget directly.
    /// </summary>
    /// <inheritdoc />
    public string TrimQueryToWindow(EmbeddingSettings settings, string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var tokens = tokenizer.CountTokens(query);
        if (tokens <= OnnxEmbeddingGenerator.MaxContentTokens)
        {
            return query;
        }

        var trimmed = TokenBudget.Trim(query, OnnxEmbeddingGenerator.MaxContentTokens,
            text => tokenizer.CountTokens(text));
        Log.QueryTrimmedToWindow(_logger, tokens, OnnxEmbeddingGenerator.MaxContentTokens, trimmed.Length, query.Length);
        return trimmed;
    }

    public static int SafeChunkBudgetFor(string provider, string? model) =>
        provider.ToLowerInvariant() switch
        {
            "local" => OnnxEmbeddingGenerator.MaxContentTokens,
            _ => ContextTokensFor(provider, model)
        };

    /// <summary>Stable engine identity recorded in settings; a change re-embeds the bank.</summary>
    public static string EngineFingerprint(string provider, string? model, string? baseUrl) =>
        provider.ToLowerInvariant() switch
        {
            "local" => $"local:{(string.IsNullOrWhiteSpace(model) ? "bundled" : model)}",
            "openai" =>
                $"openai:{model}@{(string.IsNullOrWhiteSpace(baseUrl) ? DefaultOpenAiEndpoint : baseUrl)}",
            var other => other
        };

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

        return new OnnxEmbeddingGenerator(modelPath, BundledModel.ResolveVocabPath(), _logger);
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 416, Level = LogLevel.Warning,
            Message = "Search query was shortened to fit the embedding model: {Tokens} tokens exceeded the "
                      + "{MaxTokens}-token window, so only the first {TrimmedChars} of {OriginalChars} characters "
                      + "were used to find matches. Results may miss what the rest of the query asked for — "
                      + "use a shorter, more specific query.")]
        public static partial void QueryTrimmedToWindow(ILogger logger, int tokens, int maxTokens,
            int trimmedChars, int originalChars);
    }

}
