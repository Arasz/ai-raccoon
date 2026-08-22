using AiRaccoon.Core.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     IEmbeddingGenerator over an ONNX embedding model (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): one batched session run, then the
///     manifest-selected pooling (mean | cls | model-output) and normalization (l2 | none). The
///     bundled all-MiniLM-L6-v2 path — wordpiece tokenizer, mean-pool + L2, 256 window — is the
///     default descriptor and is behavior-preserved (G3 golden vectors).
/// </summary>
internal sealed partial class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>
    ///     Real-content token budget of the BUNDLED engine: the 256-token window minus the
    ///     [CLS]/[SEP] special tokens <see cref="Encode" /> adds via <c>addSpecialTokens: true</c> —
    ///     a chunk tokenizing to exactly this many WordPiece tokens fills the window without ever
    ///     reaching the truncation branch below (docs/adr/0036). Manifest engines resolve their own
    ///     budget through <see cref="EmbeddingService" /> (plan D6).
    /// </summary>
    public const int MaxContentTokens = 254;

    private readonly ILogger _logger;
    private readonly InferenceSession _session;
    private readonly IEmbeddingTokenizer _tokenizer;
    private readonly int _window;
    private readonly string _pooling;
    private readonly string _normalization;
    private readonly IReadOnlyList<string> _inputNames;
    private readonly string _outputName;

    /// <summary>
    ///     Output dimension reported by the ONNX session itself (engineer doc §4.2.1) — the
    ///     replacement for the deleted <c>EmbeddingMath.Dimension</c> const. Manifest-declared
    ///     dimensions are cross-checked against this at construction.
    /// </summary>
    public int Dimension { get; }

    /// <summary>ORT intra-op threads this session was built with (WP11-A/G16); 0 means ORT's own default.</summary>
    public int IntraOpThreads { get; }

    internal OnnxEmbeddingGenerator(string modelPath, IEmbeddingTokenizer tokenizer, EngineDescriptor descriptor, ILogger logger,
        int intraOpThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _logger = logger;
        _tokenizer = tokenizer;
        _window = descriptor.ContextWindowTokens;
        _pooling = descriptor.Pooling;
        _normalization = descriptor.Normalization;
        _inputNames = descriptor.InputNames;
        IntraOpThreads = intraOpThreads;
        if (intraOpThreads > 0)
        {
            using var sessionOptions = new SessionOptions { IntraOpNumThreads = intraOpThreads };
            _session = new InferenceSession(modelPath, sessionOptions);
        }
        else
        {
            _session = new InferenceSession(modelPath);
        }

        ValidateInputNames(descriptor);
        if (_pooling == "model-output" && string.IsNullOrWhiteSpace(descriptor.EmbeddingOutput))
        {
            throw new InvalidOperationException(
                $"Pooling mode 'model-output' requires an onnx.embeddingOutput (manifest model '{descriptor.Model}').");
        }

        _outputName = OutputNameFor(descriptor);
        Dimension = ReadOutputDimension(_session, _outputName, descriptor.Model);
        if (descriptor.Dimensions != Dimension)
        {
            throw new InvalidOperationException(
                $"Manifest model '{descriptor.Model}' declares {descriptor.Dimensions} dimensions but the ONNX session reports " +
                $"{Dimension} for output '{_outputName}'.");
        }

        // Adapting to the graph's rank (see Pool) is deliberate, but never silent: a manifest whose
        // pooling mode the graph makes unreachable is still a manifest to correct.
        if (_pooling != "model-output" && _session.OutputRank(_outputName) == OnnxOutputRanks.PooledRank)
        {
            Log.GraphPoolsItsOwnOutput(_logger, descriptor.Model, _pooling, _outputName);
        }
    }

    /// <summary>
    ///     Builds the same BERT WordPiece tokenizer the bundled engine embeds with, so a caller that
    ///     needs to *count* tokens the way this generator will (e.g. the chunker, for a guaranteed
    ///     budget — docs/adr/0036) uses an identically configured tokenizer rather than a
    ///     hand-duplicated copy of these options.
    /// </summary>
    public static BertTokenizer CreateTokenizer(string vocabPath) => BertTokenizer.Create(vocabPath, new BertOptions
    {
        LowerCaseBeforeTokenization = true,
        ApplyBasicTokenization = true,
        SplitOnSpecialTokens = true,
        IndividuallyTokenizeCjk = true,
        RemoveNonSpacingMarks = true
    });

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.Select(Encode).ToList();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
        if (items.Count == 0)
        {
            return Task.FromResult(embeddings);
        }

        return Task.Run(() => RunBatch(items, embeddings, cancellationToken), cancellationToken);
    }

    public void Dispose() => _session.Dispose();

    object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;

    private GeneratedEmbeddings<Embedding<float>> RunBatch(
        IReadOnlyList<(int[] Ids, int[] Mask)> items, GeneratedEmbeddings<Embedding<float>> embeddings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maxLen = Math.Min(_window, items.Max(i => i.Ids.Length));
        var batch = items.Count;

        var inputIds = new long[batch * maxLen];
        var attentionMask = new long[batch * maxLen];
        for (var i = 0; i < batch; i++)
        {
            var ids = items[i].Ids;
            var mask = items[i].Mask;
            for (var s = 0; s < maxLen; s++)
            {
                if (s < ids.Length)
                {
                    inputIds[i * maxLen + s] = ids[s];
                    attentionMask[i * maxLen + s] = mask[s];
                }
            }
        }

        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [batch, maxLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [batch, maxLen]))
        };
        if (_inputNames.Contains("token_type_ids", StringComparer.Ordinal))
        {
            feed.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[batch * maxLen], [batch, maxLen])));
        }

        using var results = _session.Run(feed);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();
        var dense = output as DenseTensor<float>
                    ?? throw new InvalidOperationException($"ONNX {_outputName} is not a dense tensor.");

        Pool(dense.Buffer.Span, dense.Dimensions, batch, maxLen, Dimension, attentionMask, _pooling, _normalization,
            _outputName, embeddings);
        return embeddings;
    }

    /// <summary>
    ///     Turns one session run's selected output into one vector per batch row. The output's own
    ///     RANK decides how, not the manifest's <c>pooling.mode</c> (issue #466): a rank-3
    ///     <c>[batch, sequence, dimensions]</c> tensor is token-level and gets that mode applied
    ///     here, while a rank-2 <c>[batch, dimensions]</c> tensor is already the embedding — a graph
    ///     that pools inside itself, which no mode can be applied to. A manifest whose mode was
    ///     inferred rather than read (<c>ModelDownloadPlanner</c>'s placeholder branch) is a guess;
    ///     the rank is a fact.
    /// </summary>
    internal static void Pool(ReadOnlySpan<float> output, ReadOnlySpan<int> dimensions, int batch, int maxLen,
        int dimension, ReadOnlySpan<long> attentionMask, string pooling, string normalization, string outputName,
        GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        if (pooling == "model-output" && (dimensions.Length != 2 || dimensions[1] != dimension))
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} must be a dense [batch, {dimension}] tensor for model-output pooling.");
        }

        if (dimensions.Length == 2)
        {
            PoolAlreadyPooledOutput(output, dimensions, batch, dimension, normalization, outputName, embeddings);
            return;
        }

        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} is a rank-{dimensions.Length} tensor; a token-embeddings output must be "
                + $"[batch, sequence, {dimension}] and an already-pooled one [batch, {dimension}].");
        }

        var maskRow = new int[maxLen];
        for (var i = 0; i < batch; i++)
        {
            for (var s = 0; s < maxLen; s++)
            {
                maskRow[s] = (int)attentionMask[i * maxLen + s];
            }

            var row = output.Slice(i * maxLen * dimension, maxLen * dimension);
            var vector = pooling switch
            {
                "cls" => normalization == "l2"
                    ? EmbeddingMath.ClsPoolAndNormalize(row, dimension)
                    : EmbeddingMath.ClsPool(row, dimension),
                // "mean" — the bundled path; mean+l2 takes the exact pre-WP3 code shape (G3).
                _ => normalization == "l2"
                    ? EmbeddingMath.MeanPoolAndNormalize(row, maskRow, maxLen, dimension)
                    : EmbeddingMath.MeanPool(row, maskRow, maxLen, dimension)
            };
            embeddings.Add(new Embedding<float>(vector));
        }
    }

    private static void PoolAlreadyPooledOutput(ReadOnlySpan<float> output, ReadOnlySpan<int> dimensions, int batch,
        int dimension, string normalization, string outputName, GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        if (dimensions[1] != dimension || output.Length < batch * dimension)
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} is a [batch, {dimensions[1]}] tensor of {output.Length} values; "
                + $"{batch} × {dimension} were expected.");
        }

        for (var i = 0; i < batch; i++)
        {
            var vector = output.Slice(i * dimension, dimension).ToArray();
            embeddings.Add(new Embedding<float>(normalization == "l2" ? EmbeddingMath.L2Normalize(vector) : vector));
        }
    }

    private static void ValidateInputNames(EngineDescriptor descriptor)
    {
        foreach (var name in descriptor.InputNames)
        {
            if (name is not ("input_ids" or "attention_mask" or "token_type_ids"))
            {
                throw new InvalidOperationException(
                    $"Manifest model '{descriptor.Model}' declares unsupported ONNX input '{name}'; " +
                    "supported inputs: input_ids, attention_mask, token_type_ids.");
            }
        }
    }

    private static string OutputNameFor(EngineDescriptor descriptor) =>
        descriptor.Pooling == "model-output" ? descriptor.EmbeddingOutput! : descriptor.TokenEmbeddingsOutput;

    private static int ReadOutputDimension(InferenceSession session, string outputName, string modelName)
    {
        if (!session.OutputMetadata.TryGetValue(outputName, out var metadata))
        {
            throw new InvalidOperationException(
                $"ONNX model '{modelName}' has no output named '{outputName}' (check the manifest's onnx output names).");
        }

        var dims = metadata.Dimensions;
        if (dims is null || dims.Length == 0 || dims[^1] <= 0)
        {
            throw new InvalidOperationException(
                $"ONNX model '{modelName}' output '{outputName}' has no static final dimension " +
                $"(dims: [{string.Join(", ", dims ?? [])}]); the embedding dimension must be a compile-time constant.");
        }

        return (int)dims[^1];
    }

    /// <summary>
    ///     A run of ~100+ characters with no space or punctuation (this tokenizer's pretokenizer does
    ///     not split on newline/tab/CR) exceeds the per-word decomposition limit and collapses to a
    ///     single [UNK] — reporting a *tiny* token count for real content, invisible to any budget
    ///     ceiling check (docs/adr/0036). Newline-joined hash/id lists are the realistic trigger.
    /// </summary>
    private const int UnkCollapseMinChars = 100;

    private (int[] Ids, int[] Mask) Encode(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, addSpecialTokens: true);
        if (ids.Count > _window)
        {
            Log.ChunkTruncatedAtEmbedTime(_logger, ids.Count, _window);
            ids = [.. ids.Take(_window)];
        }
        else if (ids.Count <= 3 && text.Length > UnkCollapseMinChars)
        {
            Log.ChunkPossiblyCollapsedToUnknownToken(_logger, text.Length, ids.Count);
        }

        var mask = new int[ids.Count];
        Array.Fill(mask, 1);
        return ([.. ids], mask);
    }

    public static partial class Log
    {
        /// <summary>
        ///     A STORED entry exceeded the window (docs/adr/0036). Should stay at zero once chunk budgets
        ///     are engine-aware — which it could not, while queries reached this same event (ADR-0071).
        /// </summary>
        [LoggerMessage(EventId = 414, Level = LogLevel.Warning,
            Message = "A stored entry was shortened before embedding: {ActualTokens} tokens exceeded the "
                      + "{MaxTokens}-token window, so the tail of that entry is missing from its search vector. "
                      + "The entry's text is intact; only what search matches on is short. Queries are trimmed "
                      + "separately and reported as event 416 — this one is always a write or an ingest.")]
        public static partial void ChunkTruncatedAtEmbedTime(ILogger logger, int actualTokens, int maxTokens);

        /// <summary>Fires when a long chunk tokenizes to almost nothing — likely an unknown-token collapse from a
        /// long punctuation-free, newline-joined run (docs/adr/0036) — and is embedded as noise. Wording is
        /// family-neutral (WP3 engineer S8): the collapse mechanism differs per tokenizer family.</summary>
        [LoggerMessage(EventId = 415, Level = LogLevel.Warning,
            Message = "Chunk possibly collapsed to an unknown token at embed time: {Chars} characters tokenized to only {ActualTokens} tokens")]
        public static partial void ChunkPossiblyCollapsedToUnknownToken(ILogger logger, int chars, int actualTokens);

        /// <summary>Issue #466: the graph emits a [batch, dimensions] vector, so the manifest's token-level
        /// pooling mode cannot apply and the graph's own pooling is used instead.</summary>
        [LoggerMessage(EventId = 417, Level = LogLevel.Warning,
            Message = "Model '{Model}' pools inside its own ONNX graph: output '{Output}' is [batch, dimensions], "
                      + "so the manifest's pooling mode '{Pooling}' cannot be applied and the graph's own vector is "
                      + "used as-is. Embedding is correct; the manifest's pooling.mode is wrong and should say "
                      + "'model-output'.")]
        public static partial void GraphPoolsItsOwnOutput(ILogger logger, string model, string pooling, string output);
    }
}
