using AiRaccoon.Benchmarks.Corpus;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>Quality aggregates recorded for one sweep point over the fixed query set.</summary>
public sealed record SweepOutcome(
    SweepPoint Point,
    double NdcgAt10,
    double NdcgAt20,
    double Mrr,
    double RecallAt10,
    double RecallAt30);

/// <summary>
///     Runner shape the P6 fusion implementation plugs into: for each matrix point it asks the
///     rank source for every query's ranked ids, then records averaged metrics. No fusion code
///     lives here yet — the matrix and the recording types are the contract.
/// </summary>
public static class SweepRunner
{
    /// <summary>Ranked doc ids for one query at one sweep point; the P6 fused retriever satisfies this.</summary>
    public delegate Task<IReadOnlyList<string>> RankSource(
        SweepPoint point, CorpusQuery query, CancellationToken cancellationToken);

    public static async Task<IReadOnlyList<SweepOutcome>> RunAsync(
        IReadOnlyList<CorpusQuery> queries, RankSource rankSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(rankSource);

        var outcomes = new List<SweepOutcome>(SweepMatrix.Points.Count);
        foreach (var point in SweepMatrix.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ndcg10 = new List<double>(queries.Count);
            var ndcg20 = new List<double>(queries.Count);
            var mrr = new List<double>(queries.Count);
            var recall10 = new List<double>(queries.Count);
            var recall30 = new List<double>(queries.Count);

            foreach (var query in queries)
            {
                var ranked = await rankSource(point, query, cancellationToken).ConfigureAwait(false);
                var relevant = query.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
                ndcg10.Add(RetrievalMetrics.NdcgAtK(ranked, relevant, 10));
                ndcg20.Add(RetrievalMetrics.NdcgAtK(ranked, relevant, 20));
                mrr.Add(RetrievalMetrics.Mrr(ranked, relevant));
                recall10.Add(RetrievalMetrics.RecallAtK(ranked, relevant, 10));
                recall30.Add(RetrievalMetrics.RecallAtK(ranked, relevant, 30));
            }

            outcomes.Add(new SweepOutcome(
                point,
                ndcg10.Average(),
                ndcg20.Average(),
                mrr.Average(),
                recall10.Average(),
                recall30.Average()));
        }

        return outcomes;
    }
}
