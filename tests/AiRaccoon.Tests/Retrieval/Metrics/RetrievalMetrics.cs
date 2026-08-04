namespace AiRaccoon.Tests.Retrieval.Metrics;

/// <summary>
///     Standard information-retrieval metrics over ranked id lists. Pure functions: no I/O, no
///     framework dependencies — the golden-retrieval harness computes every aggregate through these.
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>nDCG at cutoff k; 0 when nothing relevant is ranked or k is non-positive.</summary>
    public static double NdcgAtK(IReadOnlyList<string> rankedIds, IReadOnlySet<string> relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(rankedIds);
        ArgumentNullException.ThrowIfNull(relevant);
        if (k <= 0 || relevant.Count == 0)
        {
            return 0;
        }

        var dcg = 0.0;
        var counted = Math.Min(k, rankedIds.Count);
        for (var i = 0; i < counted; i++)
        {
            if (relevant.Contains(rankedIds[i]))
            {
                dcg += 1.0 / Math.Log2(i + 2);
            }
        }

        var ideal = 0.0;
        for (var i = 0; i < Math.Min(k, relevant.Count); i++)
        {
            ideal += 1.0 / Math.Log2(i + 2);
        }

        return dcg / ideal;
    }

    /// <summary>Reciprocal rank of the first relevant item; 0 when none is ranked.</summary>
    public static double Mrr(IReadOnlyList<string> rankedIds, IReadOnlySet<string> relevant)
    {
        ArgumentNullException.ThrowIfNull(rankedIds);
        ArgumentNullException.ThrowIfNull(relevant);

        for (var i = 0; i < rankedIds.Count; i++)
        {
            if (relevant.Contains(rankedIds[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    /// <summary>Fraction of the relevance set retrieved within the top k; 0 for empty relevance or non-positive k.</summary>
    public static double RecallAtK(IReadOnlyList<string> rankedIds, IReadOnlySet<string> relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(rankedIds);
        ArgumentNullException.ThrowIfNull(relevant);
        if (k <= 0 || relevant.Count == 0)
        {
            return 0;
        }

        var hits = 0;
        var counted = Math.Min(k, rankedIds.Count);
        for (var i = 0; i < counted; i++)
        {
            if (relevant.Contains(rankedIds[i]))
            {
                hits++;
            }
        }

        return (double)hits / relevant.Count;
    }

    /// <summary>
    ///     Kendall rank-agreement over the pairs both lists share: (concordant - discordant) /
    ///     (concordant + discordant). Lists must hold distinct ids. 0 when fewer than two ids
    ///     are common (no pair evidence).
    /// </summary>
    public static double KendallTau(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var rankInFirst = RankMap(first);
        var rankInSecond = RankMap(second);
        var common = rankInFirst.Keys.Where(rankInSecond.ContainsKey).ToList();

        if (common.Count < 2)
        {
            return 0;
        }

        var concordant = 0;
        var discordant = 0;
        for (var i = 0; i < common.Count; i++)
        {
            for (var j = i + 1; j < common.Count; j++)
            {
                var orderedSame = (rankInFirst[common[i]] < rankInFirst[common[j]])
                    == (rankInSecond[common[i]] < rankInSecond[common[j]]);
                if (orderedSame)
                {
                    concordant++;
                }
                else
                {
                    discordant++;
                }
            }
        }

        return (double)(concordant - discordant) / (concordant + discordant);
    }

    private static Dictionary<string, int> RankMap(IReadOnlyList<string> ids)
    {
        var map = new Dictionary<string, int>(ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            map[ids[i]] = i;
        }

        return map;
    }
}
