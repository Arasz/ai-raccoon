using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Benchmarks.Embedders;
using BenchmarkDotNet.Attributes;

namespace AiRaccoon.Benchmarks.Benchmarks;

/// <summary>
///     Measures single-query retrieval latency through each embedding backend.
///     Quality metrics are computed once per model in the runner, not here.
/// </summary>
[MemoryDiagnoser]
public class EmbeddingLatencyBenchmark : IDisposable
{
    private IEmbedder? _active;

    [ParamsSource(nameof(EmbedderNames))] public string Embedder { get; set; } = "";

    public void Dispose() => (_active as IDisposable)?.Dispose();

    public static IEnumerable<string> EmbedderNames() => EmbedderCatalog.Names;

    [GlobalSetup]
    public void Setup()
    {
        _active = EmbedderCatalog.Create(Embedder);
        _active.IndexAsync(BenchmarkCorpus.Documents).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task Search() => await _active!.SearchAsync(BenchmarkCorpus.Queries[0].Text, 10);
}
