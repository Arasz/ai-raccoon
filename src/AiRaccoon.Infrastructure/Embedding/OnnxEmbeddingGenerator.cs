using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     IEmbeddingGenerator over the bundled int8 all-MiniLM-L6-v2 ONNX model (FR-NM-3; see docs/work/features-native-memory/native-memory.feature):
///     BERT WordPiece tokenization, one batched session run, mean-pool + L2-normalize
///     matching sentence-transformers semantics.
/// </summary>
internal sealed class OnnxEmbeddingGenerator(string modelPath, string vocabPath) : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int MaxSequenceLength = 256;

    private readonly InferenceSession _session = new(modelPath);

    private readonly BertTokenizer _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
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

    private (int[] Ids, int[] Mask) Encode(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, true, true,
            true);
        if (ids.Count > MaxSequenceLength)
        {
            ids = ids.Take(MaxSequenceLength).ToList();
        }

        var mask = new int[ids.Count];
        Array.Fill(mask, 1);
        return (ids.ToArray(), mask);
    }
}
