using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Globalization;
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
public sealed partial class EmbeddingService(ILogger<EmbeddingService> logger, ILocalTokenizer localTokenizer,
    ITokenizerFactory tokenizerFactory, IEmbeddingManifestLoader manifestDescriptor, ISettingsStore? settingsStore = null)
    : IEmbeddingService
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

    // D1 cost note: Load() now hashes every pinned file's bytes (the ~90 MB ONNX weights
    // included). ManifestDescriptorFor/ManifestContentBudget sit on the write hot path
    // (FileIngestor, SqliteMemoryStore's ChunkToBudgetAsync) — caching the descriptor per engine
    // fingerprint keeps that hash to once per process per engine, never per call (plan D1).
    private readonly ConcurrentDictionary<string, EngineDescriptor> _descriptors =
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
    ///     The dimension the configured engine embeds at (D3), so the drain reconciles vec0 to it
    ///     before writing. Remote providers declare theirs in `embedding.dimensions`; until that row
    ///     exists the legacy 384 assumption stands, which WP4's pre-commit probe replaces with a
    ///     fail-closed refusal.
    /// </summary>
    public int ResolveDimensions(EmbeddingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(settings.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return settings.Dimensions ?? LegacyManifestSemantics.LegacyDimensions;
        }

        return ManifestDescriptorFor(settings.Model)?.Dimensions ?? BundledDescriptor.Dimensions;
    }

    /// <summary>The manifest descriptor for a local model directory, or null for bundled/legacy paths.</summary>
    private EngineDescriptor? ManifestDescriptorFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var full = Path.GetFullPath(model);
        return Directory.Exists(full) ? LoadManifestDescriptorCached(model, full) : null;
    }

    /// <summary>Loads (and caches per engine fingerprint) the manifest descriptor for a directory
    /// already confirmed to exist. Not cached on throw — a missing/invalid manifest keeps
    /// re-throwing its own actionable message on every call, same as before this cache existed.</summary>
    private EngineDescriptor LoadManifestDescriptorCached(string model, string full) =>
        _descriptors.GetOrAdd(EngineFingerprint("local", model, null), _ => manifestDescriptor.Load(full));

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
    public string EngineFingerprint(string provider, string? model, string? baseUrl)
    {
        var lower = provider.ToLowerInvariant();
        if (lower == "local" && !string.IsNullOrWhiteSpace(model) && Directory.Exists(model))
        {
            var manifestPath = Path.Combine(Path.GetFullPath(model), EmbeddingManifest.FileName);
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

    /// <summary>
    ///     WP11-A/G16: an unset setting halves the core count (never below 1); "0" means ORT's own
    ///     default; anything else that doesn't parse to a non-negative integer is a garbage value —
    ///     logged and treated as unset. <paramref name="coreCount" /> is the seam a test drives with
    ///     10 or 1; production passes the real core count.
    /// </summary>
    internal int ResolveThreadCount(string? rawSetting, int coreCount)
    {
        if (string.IsNullOrWhiteSpace(rawSetting))
        {
            return HalvedCoreThreadDefault(coreCount);
        }

        if (TryParseThreadsSetting(rawSetting, out var threads))
        {
            return threads;
        }

        Log.InvalidThreadsSetting(_logger, rawSetting);
        return HalvedCoreThreadDefault(coreCount);
    }

    /// <summary>The halving rule alone (#522): shared by <see cref="ResolveThreadCount" /> and doctor's threads line, so the default is computed in exactly one place.</summary>
    internal static int HalvedCoreThreadDefault(int coreCount) => Math.Max(1, coreCount / 2);

    /// <summary>True when <paramref name="rawSetting" /> is a usable explicit thread count (0 = ORT default); shared with doctor (#522).</summary>
    internal static bool TryParseThreadsSetting(string? rawSetting, out int threads) =>
        int.TryParse(rawSetting, NumberStyles.Integer, CultureInfo.InvariantCulture, out threads) && threads >= 0;

    /// <summary>#522: "setting" when <paramref name="rawSetting" /> resolved to an explicit value, else the halved-core default was used.</summary>
    internal static string ThreadCountSource(string? rawSetting) =>
        TryParseThreadsSetting(rawSetting, out _) ? "setting" : "halved-core default";

    private IEmbeddingGenerator<string, Embedding<float>> CreateLocal(EmbeddingSettings settings)
    {
        // One-time per fingerprint (cached by _engines): sessions are rebuilt only on restart, so a
        // blocking read here costs nothing recurring.
        var rawThreads = settingsStore?.GetSettingAsync(EmbeddingSettingsKeys.Threads, CancellationToken.None)
            .GetAwaiter().GetResult();
        var threads = ResolveThreadCount(rawThreads, Environment.ProcessorCount);

        var modelPath = string.IsNullOrWhiteSpace(settings.Model)
            ? BundledModel.ResolveModelPath()
            : Path.GetFullPath(settings.Model);

        OnnxEmbeddingGenerator generator;
        if (Directory.Exists(modelPath))
        {
            // Directory activation (M3): a directory REQUIRES a manifest — only the legacy
            // .onnx-file path keeps the bundled defaults. Any dimension is loadable now that the
            // drain reconciles vec0 to the engine's dimension before writing (WP4/D3).
            var descriptor = manifestDescriptor.Load(modelPath);
            var tokenizer = ResolveManifestTokenizer(modelPath)!;
            generator = new OnnxEmbeddingGenerator(Path.Combine(modelPath, descriptor.OnnxModelFile), tokenizer, descriptor, _logger, threads);
        }
        else
        {
            // Fail fast on a missing/name-shaped path instead of a cryptic ONNX NoSuchFile error.
            if (!File.Exists(modelPath))
            {
                throw new InvalidOperationException(
                    $"Configured embedding model '{modelPath}' does not exist (it may be a model name, not a path; ~ is not expanded). " +
                    "Run 'ai-raccoon model set local' for the bundled model, or 'ai-raccoon model set local <path-to-onnx>' for a custom path.");
            }

            var bundledTokenizer = _tokenizers.GetOrAdd("bundled",
                _ => WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()));
            generator = new OnnxEmbeddingGenerator(modelPath, bundledTokenizer, BundledDescriptor, _logger, threads);
        }

        // #522: the only observable confirmation that the resolved thread count took effect —
        // doctor shows what a setting resolves to, this shows what a real session was built with.
        EmbeddingSessionLog.EmbeddingSessionCreated(_logger, generator.IntraOpThreads, ThreadCountSource(rawThreads));
        return generator;
    }

    private IEmbeddingTokenizer? ResolveManifestTokenizer(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !Directory.Exists(model))
        {
            return null;
        }

        var manifestPath = Path.Combine(Path.GetFullPath(model), EmbeddingManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var fingerprint = EngineFingerprint("local", model, null);
        return _tokenizers.GetOrAdd(fingerprint, _ =>
        {
            var descriptor = manifestDescriptor.Load(Path.GetFullPath(model));
            return tokenizerFactory.Create(descriptor, Path.GetFullPath(model));
        });
    }

    /// <summary>The D6 content budget for a manifest model, or null when the model is not manifest-based.</summary>
    private int? ManifestContentBudget(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var full = Path.GetFullPath(model);
        if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, EmbeddingManifest.FileName)))
        {
            return null;
        }

        var descriptor = LoadManifestDescriptorCached(model, full);
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
        // Moved 416 -> 418 (#466): OnnxEmbeddingGenerator's block ran 414-415 and needed 417, which
        // 416 wedged shut. 416 is retired, not reused — see docs/reference/logging-event-ids.md.
        [LoggerMessage(EventId = 418, Level = LogLevel.Warning,
            Message = "Search query was shortened to fit the embedding model: {Tokens} tokens exceeded the "
                      + "{MaxTokens}-token window, so only the first {TrimmedChars} of {OriginalChars} characters "
                      + "were used to find matches. Results may miss what the rest of the query asked for — "
                      + "use a shorter, more specific query.")]
        public static partial void QueryTrimmedToWindow(ILogger logger, int tokens, int maxTokens,
            int trimmedChars, int originalChars);

        /// <summary>WP11-A/G16: embedding.threads didn't parse to a non-negative integer; the halved-core default is used instead.</summary>
        [LoggerMessage(EventId = 419, Level = LogLevel.Warning,
            Message = "Invalid embedding.threads setting '{Value}': expected a non-negative integer (0 = ORT default). "
                      + "Using max(1, logicalCores/2) instead.")]
        public static partial void InvalidThreadsSetting(ILogger logger, string value);
    }
}

/// <summary>
///     #522: EventId 426, its own owner block. `OnnxEmbeddingGenerator` (414-415, 417) and
///     `EmbeddingService` (418-419) are both wedged against their neighbours (416 retired,
///     420-425 taken by `NoOpCodeChunker`/`CodeEmbedder`/`ManifestPoolingRepair`) — neither block
///     could grow without its range engulfing another owner's, which
///     <c>LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners</c> forbids. A
///     dedicated single-id owner, immediately after that cluster, avoids renumbering any of them.
///     See docs/reference/logging-event-ids.md.
/// </summary>
internal static partial class EmbeddingSessionLog
{
    [LoggerMessage(EventId = 426, Level = LogLevel.Information,
        Message = "Embedding session created: intra-op threads {Threads} ({Source})")]
    public static partial void EmbeddingSessionCreated(ILogger logger, int threads, string source);
}
