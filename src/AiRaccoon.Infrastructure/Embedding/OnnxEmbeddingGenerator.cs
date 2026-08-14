using AiRaccoon.Core.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     IEmbeddingGenerator over the bundled int8 all-MiniLM-L6-v2 ONNX model (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature):
///     BERT WordPiece tokenization, one batched session run, mean-pool + L2-normalize
///     matching sentence-transformers semantics.
/// </summary>
internal sealed partial class OnnxEmbeddingGenerator(string modelPath, string vocabPath, ILogger logger)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int MaxSequenceLength = 256;

    /// <summary>
    ///     Real-content token budget: <see cref="MaxSequenceLength" /> minus the [CLS]/[SEP] special
    ///     tokens <see cref="Encode" /> adds via <c>addSpecialTokens: true</c> — a chunk tokenizing to
    ///     exactly this many WordPiece tokens fills the window without ever reaching the truncation
    ///     branch below (docs/adr/0036).
    /// </summary>
    public const int MaxContentTokens = MaxSequenceLength - 2;

    private readonly ILogger _logger = logger;
    private readonly InferenceSession _session = new(modelPath);

    private readonly BertTokenizer _tokenizer = CreateTokenizer(vocabPath);

    /// <summary>
    ///     Builds the same BERT WordPiece tokenizer this generator embeds with, so a caller that
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
        var maxLen = Math.Min(MaxSequenceLength, items.Max(i => i.Ids.Length));
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

        using var results = _session.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [batch, maxLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [batch, maxLen])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[batch * maxLen], [batch, maxLen]))
        ]);

        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
        var dense = hidden as DenseTensor<float>
                    ?? throw new InvalidOperationException("ONNX last_hidden_state is not a dense tensor.");
        var maskRow = new int[maxLen];
        for (var i = 0; i < batch; i++)
        {
            for (var s = 0; s < maxLen; s++)
            {
                maskRow[s] = (int)attentionMask[i * maxLen + s];
            }

            var vector = EmbeddingMath.MeanPoolAndNormalize(
                dense.Buffer.Slice(i * maxLen * EmbeddingMath.Dimension, maxLen * EmbeddingMath.Dimension).Span,
                maskRow, maxLen, EmbeddingMath.Dimension);
            embeddings.Add(new Embedding<float>(vector));
        }

        return embeddings;
    }

    /// <summary>
    ///     A run of ~100+ characters with no space or punctuation (this tokenizer's pretokenizer does
    ///     not split on newline/tab/CR) exceeds WordPiece's per-word length limit and collapses to a
    ///     single [UNK] — reporting a *tiny* token count for real content, invisible to any budget
    ///     ceiling check (docs/adr/0036). Newline-joined hash/id lists are the realistic trigger.
    /// </summary>
    private const int UnkCollapseMinChars = 100;

    private (int[] Ids, int[] Mask) Encode(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, true, true,
            true);
        if (ids.Count > MaxSequenceLength)
        {
            Log.ChunkTruncatedAtEmbedTime(_logger, ids.Count, MaxSequenceLength);
            ids = [.. ids.Take(MaxSequenceLength)];
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
        /// <summary>Fires when a chunk's real BERT WordPiece length exceeds the model's window and gets
        /// silently truncated (docs/adr/0036) — should stay at zero once chunk budgets are engine-aware.</summary>
        [LoggerMessage(EventId = 414, Level = LogLevel.Warning,
            Message = "Chunk truncated at embed time: {ActualTokens} BERT WordPiece tokens exceed the bundled model's {MaxTokens}-token window")]
        public static partial void ChunkTruncatedAtEmbedTime(ILogger logger, int actualTokens, int maxTokens);

        /// <summary>Fires when a long chunk tokenizes to almost nothing — likely an [UNK] collapse from a
        /// long punctuation-free, newline-joined run (docs/adr/0036) — and is embedded as noise.</summary>
        [LoggerMessage(EventId = 415, Level = LogLevel.Warning,
            Message = "Chunk possibly collapsed to [UNK] at embed time: {Chars} characters tokenized to only {ActualTokens} BERT WordPiece tokens")]
        public static partial void ChunkPossiblyCollapsedToUnknownToken(ILogger logger, int chars, int actualTokens);
    }
}
