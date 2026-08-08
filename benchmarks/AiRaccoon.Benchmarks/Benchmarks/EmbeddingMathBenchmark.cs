using AiRaccoon.Core.Embedding;
using BenchmarkDotNet.Attributes;

namespace AiRaccoon.Benchmarks.Benchmarks;

/// <summary>
///     Measures <see cref="EmbeddingMath.MeanPoolAndNormalize" /> at the fixed model dimension
///     (384) across sequence length and mask density. Mask is laid out active-first, then
///     padding, matching <c>OnnxEmbeddingGenerator.RunBatch</c>. <c>MaskDensity = 0</c> is a
///     public-API edge (the early-return path), not a reachable production state — every
///     tokenized item carries at least <c>[CLS] [SEP]</c>.
/// </summary>
[MemoryDiagnoser]
public class EmbeddingMathBenchmark
{
    private const int Dim = EmbeddingMath.Dimension;

    private float[] _hidden = [];
    private int[] _mask = [];

    [Params(16, 64, 256)] public int SeqLen { get; set; }

    [Params(1.0, 0.5, 0.0)] public double MaskDensity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260808);
        _hidden = new float[SeqLen * Dim];
        for (var i = 0; i < _hidden.Length; i++)
        {
            _hidden[i] = (float)(random.NextDouble() * 2 - 1);
        }

        var active = (int)(SeqLen * MaskDensity);
        _mask = new int[SeqLen];
        for (var s = 0; s < active; s++)
        {
            _mask[s] = 1;
        }
    }

    [Benchmark(Baseline = true)]
    public float[] Scalar() => MeanPoolAndNormalizeScalar(_hidden, _mask, SeqLen, Dim);

    [Benchmark]
    public float[] Vectorized() => EmbeddingMath.MeanPoolAndNormalize(_hidden, _mask, SeqLen, Dim);

    /// <summary>
    ///     Frozen pre-WP-4 copy of <see cref="EmbeddingMath.MeanPoolAndNormalize" />'s scalar
    ///     body — a speed reference only, not a correctness oracle (see D3 in
    ///     docs/plans/2026-08-08-embedding-perf.md). Correctness is gated by
    ///     EmbeddingMathTests; if this copy ever drifts from history, the worst outcome is a
    ///     stale ratio.
    /// </summary>
    private static float[] MeanPoolAndNormalizeScalar(ReadOnlySpan<float> hidden, ReadOnlySpan<int> mask, int seqLen, int dim)
    {
        var pooled = new float[dim];
        var active = 0;
        for (var s = 0; s < seqLen; s++)
        {
            if (mask[s] == 0)
            {
                continue;
            }

            active++;
            var offset = s * dim;
            for (var d = 0; d < dim; d++)
            {
                pooled[d] += hidden[offset + d];
            }
        }

        if (active == 0)
        {
            return pooled;
        }

        for (var d = 0; d < dim; d++)
        {
            pooled[d] /= active;
        }

        double lengthSquared = 0;
        foreach (var v in pooled)
        {
            lengthSquared += (double)v * v;
        }

        var norm = MathF.Sqrt((float)lengthSquared);
        if (!(norm > 0))
        {
            return pooled;
        }

        for (var d = 0; d < dim; d++)
        {
            pooled[d] /= norm;
        }

        return pooled;
    }
}
