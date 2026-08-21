using AiRaccoon.Core.Chunking;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Resolves the bank's embedding engine from settings (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): local → the bundled int8 ONNX model
///     in-process (or a manifest-driven local model directory, WP3), openai → any OpenAI-compatible
///     endpoint. A fingerprint change triggers a full re-embed (D7: a manifest's content — including
///     per-file sha256s — is part of the local fingerprint, so re-downloaded weights re-embed).
/// </summary>
public sealed partial class EmbeddingService(ILogger<EmbeddingService> logger, ILocalTokenizer localTokenizer) : IEmbeddingService
{
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";

    /// <summary>
    ///     Maximum input tokens of the bundled all-MiniLM-L6-v2 model (see docs/work/archive/2026-08-03-native-memory-plan.md
    ///     §8).
    /// </summary>
    public const int BundledModelContextTokens = 256;

    /// <summary>Documented maximum input of OpenAI-compatible text-embedding models (all share 8191).</summary>
    public const int OpenAiEmbeddingContextTokens = 8191;

    /// <summary>
    ///     Content-token chunk budget cap for manifest-local models (plan D6): 512 minus the two
    ///     special tokens every supported family reserves at embed time — expressed as a derivation
    ///     from <see cref="EngineDescriptor.DefaultSpecialTokenReservation" />, never a magic literal.
    /// </summary>
    public const int MaxManifestChunkTokens = 512 - EngineDescriptor.DefaultSpecialTokenReservation;

    private readonly ILogger<EmbeddingService> _logger = logger;

    // The service owns generator lifetimes: an ONNX session (23 MB model) and an OpenAI client
    // are expensive to build, so engines are cached per fingerprint and never disposed by callers.
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator<string, Embedding<float>>> _engines =
        new(StringComparer.Ordinal);

    // Tokenizers are cached per engine fingerprint and SHARED with the generator built for the
    // same fingerprint — the ADR-0036 invariant (the counter and the embedder are the same object)
    // holds by construction.
    private readonly ConcurrentDictionary<string, IEmbeddingTokenizer> _tokenizers =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     The compiled-in descriptor for the bundled engine — the D1 manifest equivalent of today's
    ///     constants (mean, 384, 256, wordpiece, 5 BertOptions), so the bundled path and the
    ///     manifest path share one engine shape.
    /// </summary>
    internal static EngineDescriptor BundledDescriptor { get; } = new(
        Model: "bundled all-MiniLM-L6-v2 (int8)",
        SourceRepo: "sentence-transformers/all-MiniLM-L6-v2",
        SourceRevision: null,
        Dimensions: 384,
        ContextWindowTokens: BundledModelContextTokens,
        Normalization: "l2",
        Pooling: "mean",
        SpecialTokenReservation: EngineDescriptor.DefaultSpecialTokenReservation,
        TokenizerFamily: "bert-wordpiece",
        TokenizerFile: "vocab.txt",
        SentencePieceOptions: null,
        RequiresTokenTypeIds: true,
        InputNames: ["input_ids", "attention_mask", "token_type_ids"],
        TokenEmbeddingsOutput: "last_hidden_state",
        EmbeddingOutput: null,
        OnnxModelFile: BundledModel.ModelFileName,
        Files: []);

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
    ///     Static and manifest-blind (bundled defaults) — manifest-aware resolution goes through
    ///     <see cref="ResolveChunkBudgetFor" />.
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

        var manifestBudget = ManifestContentBudget(settings.Model);
        if (manifestBudget is not null)
        {
            var tokenizer = ResolveManifestTokenizer(settings.Model)!;
            var tokens = tokenizer.CountTokens(query);
            if (tokens <= manifestBudget.Value)
            {
                return query;
            }

            var trimmed = TokenBudget.Trim(query, manifestBudget.Value, tokenizer.CountTokens);
            Log.QueryTrimmedToWindow(_logger, tokens, manifestBudget.Value, trimmed.Length, query.Length);
            return trimmed;
        }

        var bundledTokens = localTokenizer.CountTokens(query);
        if (bundledTokens <= OnnxEmbeddingGenerator.MaxContentTokens)
        {
            return query;
        }

        var bundledTrimmed = TokenBudget.Trim(query, OnnxEmbeddingGenerator.MaxContentTokens, localTokenizer.CountTokens);
        Log.QueryTrimmedToWindow(_logger, bundledTokens, OnnxEmbeddingGenerator.MaxContentTokens, bundledTrimmed.Length, query.Length);
        return bundledTrimmed;
    }

    /// <summary>
    ///     Static bundled-default budget (254 local / window for others), kept for legacy callers.
    ///     Manifest-aware resolution: <see cref="ResolveChunkBudgetFor" />.
    /// </summary>
    public static int SafeChunkBudgetFor(string provider, string? model) =>
        provider.ToLowerInvariant() switch
        {
            "local" => OnnxEmbeddingGenerator.MaxContentTokens,
            _ => ContextTokensFor(provider, model)
        };

    /// <summary>
    ///     The engine's real content-token chunk budget, resolved per engine (D6/D9): bundled and
    ///     legacy local stay 254; manifest-local models get <c>min(510, ctx − 2)</c> (the ONLY WP3
    ///     behavior change, confined to manifest models); openai/unknown keep today's min(256, 8191)
    ///     = 256 cap.
    /// </summary>
    public int ResolveChunkBudgetFor(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestContentBudget(settings.Model)
                   ?? OnnxEmbeddingGenerator.MaxContentTokens;
        }

        return Math.Min(BundledModelContextTokens, ContextTokensFor(settings.Provider, settings.Model));
    }

    /// <summary>
    ///     The tokenizer the configured LOCAL engine will embed with (D9): the manifest tokenizer
    ///     for manifest models, the bundled wordpiece tokenizer otherwise. Null for non-local
    ///     providers — they keep the chunker's default o200k proxy (unchanged). Instances are
    ///     cached per engine fingerprint and shared with the generator.
    /// </summary>
    public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (ResolveManifestTokenizer(settings.Model) is { } manifestTokenizer)
        {
            return manifestTokenizer;
        }

        return _tokenizers.GetOrAdd("bundled", _ => WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()));
    }

    /// <summary>
    ///     Stable engine identity recorded in settings; a change re-embeds the bank. Local manifest
    ///     directories hash the manifest's content INCLUDING its per-file sha256s (D7): a
    ///     re-download with new weights changes the file hashes, so the fingerprint changes and the
    ///     re-embed fires. Bundled and legacy-file identities are unchanged.
    /// </summary>
    public static string EngineFingerprint(string provider, string? model, string? baseUrl)
    {
        var lower = provider.ToLowerInvariant();
        if (lower == "local" && !string.IsNullOrWhiteSpace(model) && Directory.Exists(model))
        {
            var manifestPath = Path.Combine(Path.GetFullPath(model), "manifest.json");
            if (File.Exists(manifestPath))
            {
                return $"local:{Path.GetFullPath(model)}#{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant()}";
            }
        }

        return lower switch
        {
            "local" => $"local:{(string.IsNullOrWhiteSpace(model) ? "bundled" : model)}",
            "openai" =>
                $"openai:{model}@{(string.IsNullOrWhiteSpace(baseUrl) ? DefaultOpenAiEndpoint : baseUrl)}",
            var other => other
        };
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateLocal(EmbeddingSettings settings)
    {
        var modelPath = string.IsNullOrWhiteSpace(settings.Model)
            ? BundledModel.ResolveModelPath()
            : Path.GetFullPath(settings.Model);

        if (Directory.Exists(modelPath))
        {
            // WP3 directory activation (M3): a directory REQUIRES a manifest.json — only the legacy
            // .onnx-file path keeps the bundled defaults. M4 refuses non-384 manifests here.
            var descriptor = ProvisionalManifestDescriptor.Load(modelPath);
            ProvisionalManifestDescriptor.RequireWp3Supported(descriptor);
            var tokenizer = ResolveManifestTokenizer(modelPath)!;
            return new OnnxEmbeddingGenerator(Path.Combine(modelPath, descriptor.OnnxModelFile), tokenizer, descriptor, _logger);
        }

        // Fail fast on a missing/name-shaped path instead of a cryptic ONNX NoSuchFile error.
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"Configured embedding model '{modelPath}' does not exist (it may be a model name, not a path; ~ is not expanded). " +
                "Run 'ai-raccoon model set local' for the bundled model, or 'ai-raccoon model set local <path-to-onnx>' for a custom path.");
        }

        var bundledTokenizer = _tokenizers.GetOrAdd("bundled",
            _ => WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()));
        return new OnnxEmbeddingGenerator(modelPath, bundledTokenizer, BundledDescriptor, _logger);
    }

    private IEmbeddingTokenizer? ResolveManifestTokenizer(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !Directory.Exists(model))
        {
            return null;
        }

        var manifestPath = Path.Combine(Path.GetFullPath(model), "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var fingerprint = EngineFingerprint("local", model, null);
        return _tokenizers.GetOrAdd(fingerprint, _ =>
        {
            var descriptor = ProvisionalManifestDescriptor.Load(Path.GetFullPath(model));
            ProvisionalManifestDescriptor.RequireWp3Supported(descriptor);
            return EmbeddingTokenizerFactory.Create(descriptor, Path.GetFullPath(model));
        });
    }

    /// <summary>The D6 content budget for a manifest model, or null when the model is not manifest-based.</summary>
    private static int? ManifestContentBudget(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !Directory.Exists(model)
            || !File.Exists(Path.Combine(Path.GetFullPath(model), "manifest.json")))
        {
            return null;
        }

        var descriptor = ProvisionalManifestDescriptor.Load(Path.GetFullPath(model));
        return Math.Min(MaxManifestChunkTokens,
            descriptor.ContextWindowTokens - descriptor.SpecialTokenReservation);
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
