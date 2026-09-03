using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Benchmarks.Embedders;
using AiRaccoon.Benchmarks.Metrics;
using BenchmarkDotNet.Running;

namespace AiRaccoon.Benchmarks;

public static class Program
{
    private const string BenchmarkArg = "--bench";

    public static int Main(string[] args)
    {
        if (!args.Contains(BenchmarkArg))
        {
            return RunQualityComparison();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run([.. args.Where(a => a != BenchmarkArg)]);
        return 0;
    }

    /// <summary>
    ///     Retrieval-quality comparison across the configured embedding backends: same corpus,
    ///     same queries, same metrics — Recall@5, Recall@10, MRR, nDCG@10. One-shot (no
    ///     BenchmarkDotNet harness) so it can run in CI and against a live LM Studio server.
    /// </summary>
    private static int RunQualityComparison()
    {
        Console.WriteLine("ai-raccoon retrieval benchmark — embedding model comparison");
        Console.WriteLine($"corpus: {BenchmarkCorpus.Documents.Count} docs, {BenchmarkCorpus.Queries.Count} queries");
        Console.WriteLine();

        Console.WriteLine($"{"embedder",-52} {"dim",-6} {"R@5",-7} {"R@10",-7} {"MRR",-7} {"nDCG@10",-8}");
        Console.WriteLine(new string('-', 87));

        var failures = 0;
        foreach (var name in EmbedderCatalog.Names)
        {
            IEmbedder? embedder = null;
            try
            {
                embedder = EmbedderCatalog.Create(name);

                embedder.IndexAsync(BenchmarkCorpus.Documents).GetAwaiter().GetResult();
                var metrics = RetrievalMetricsEvaluator.Compute(embedder, BenchmarkCorpus.Queries, 10);

                Console.WriteLine(
                    $"{name,-52} {embedder.Dimensions,-6} " +
                    $"{metrics.RecallAt5,-7:F3} {metrics.RecallAt10,-7:F3} " +
                    $"{metrics.Mrr,-7:F3} {metrics.NdcgAt10,-8:F3}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"{name,-52} ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null)
                {
                    Console.WriteLine($"    inner: {ex.InnerException.Message}");
                }
            }
            finally
            {
                (embedder as IDisposable)?.Dispose();
            }
        }

        Console.WriteLine();
        if (failures > 0)
        {
            Console.WriteLine(
                $"{failures} embedder(s) failed — check AIRACCOON_TEST_GGUF (local) and LMSTUDIO_BASE_URL (default http://localhost:1234).");
            return 1;
        }

        Console.WriteLine("done.");
        return 0;
    }
}
