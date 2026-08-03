using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Benchmarks.Embedders;

namespace AiRaccoon.Benchmarks.Metrics;

/// <summary>Retrieval quality aggregates over the corpus.</summary>
public sealed record RetrievalMetrics(
    double RecallAt5,
    double RecallAt10,
    double Mrr,
    double NdcgAt10,
    int Queries);

/// <summary>Standard information-retrieval metrics over ranked retrieval results.</summary>
public static class RetrievalMetricsEvaluator
{
    public static RetrievalMetrics Compute(
        IEmbedder embedder, IReadOnlyList<CorpusQuery> queries, int topK)
    {
        var recalls5 = new List<double>();
        var recalls10 = new List<double>();
        var mrrValues = new List<double>();
        var ndcg10 = new List<double>();

        foreach (var query in queries)
        {
            var relevant = query.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            var hits = embedder.SearchAsync(query.Text, topK).GetAwaiter().GetResult();

            recalls5.Add(RecallAtK(hits, relevant, 5));
            recalls10.Add(RecallAtK(hits, relevant, 10));
            mrrValues.Add(Mrr(hits, relevant));
            ndcg10.Add(NdcgAtK(hits, relevant, 10));
        }

        return new RetrievalMetrics(
            recalls5.Average(),
            recalls10.Average(),
            mrrValues.Average(),
            ndcg10.Average(),
            queries.Count);
    }

    private static double RecallAtK(IReadOnlyList<RetrievalHit> hits, HashSet<string> relevant, int k)
    {
        if (relevant.Count == 0)
        {
            return 0;
        }

        var retrieved = hits.Take(k).Count(h => relevant.Contains(h.DocumentId));
        return (double)retrieved / relevant.Count;
    }

    private static double Mrr(IReadOnlyList<RetrievalHit> hits, HashSet<string> relevant)
    {
        for (var i = 0; i < hits.Count; i++)
        {
            if (relevant.Contains(hits[i].DocumentId))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    private static double NdcgAtK(IReadOnlyList<RetrievalHit> hits, HashSet<string> relevant, int k)
    {
        var dcg = hits.Take(k).Select((hit, i) =>
                relevant.Contains(hit.DocumentId) ? 1.0 / Math.Log2(i + 2) : 0.0)
            .Sum();

        var ideal = Enumerable.Range(0, Math.Min(k, relevant.Count))
            .Select(i => 1.0 / Math.Log2(i + 2))
            .Sum();

        return ideal > 0 ? dcg / ideal : 0;
    }
}
