using AiRaccoon.Core.Embedding;
using BenchmarkDotNet.Attributes;

namespace AiRaccoon.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class EmbeddingMathBenchmark
{
    public ArraySegment<float> Hiidden { get; set; }
    public ArraySegment<int> Mask { get; set; }
    public int SeqLen { get; set; }
    public int Dim { get; set; }

    [Benchmark(Baseline = true)]
    public float[] MeanPoolAndNormalizeDefaultImplementation() => EmbeddingMath.MeanPoolAndNormalize(Hiidden, Mask, SeqLen, Dim);
}
