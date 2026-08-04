using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Source-affinity ranking (plan C Wave 3; docs/adr/0005): adjacent-chunk boost, source
///     consolidation, document-first tie-break over the fused list. λ = 0 is a no-op.
/// </summary>
internal static class SourceAffinityRanker
{
    public static IReadOnlyList<MemorySearchResult> Rank(
        IReadOnlyList<MemorySearchResult> candidates,
        double lambda,
        double consolidationThreshold,
        DocScoreFormula formula)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (lambda <= 0.0 || candidates.Count == 0)
        {
            return candidates;
        }

        var maxRaw = candidates.Max(candidate => candidate.Ranking);
        var bySource = candidates
            .Where(candidate => candidate.SourceFile is not null)
            .GroupBy(candidate => candidate.SourceFile!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var scores = candidates.ToDictionary(
            candidate => candidate.Hash,
            candidate => candidate.Ranking
                        + lambda * SiblingCount(candidate, bySource, maxRaw, consolidationThreshold),
            StringComparer.Ordinal);

        var docScore = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate.SourceFile is null)
            {
                continue;
            }

            var score = scores[candidate.Hash];
            docScore[candidate.SourceFile] = formula == DocScoreFormula.Sum
                ? docScore.GetValueOrDefault(candidate.SourceFile) + score
                : Math.Max(docScore.GetValueOrDefault(candidate.SourceFile), score);
        }

        var order = candidates
            .OrderByDescending(candidate => scores[candidate.Hash])
            .ThenByDescending(candidate => candidate.SourceFile is null
                ? scores[candidate.Hash]
                : docScore[candidate.SourceFile])
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ChunkIndex)
            .ToList();

        var consolidated = Consolidate(order, scores, consolidationThreshold);
        var maxScore = consolidated.Count == 0 ? 1.0 : consolidated.Max(candidate => scores[candidate.Hash]);
        return [.. consolidated.Select(candidate => candidate with { Ranking = scores[candidate.Hash] / maxScore })];
    }

    private static int SiblingCount(
        MemorySearchResult candidate,
        IReadOnlyDictionary<string, List<MemorySearchResult>> bySource,
        double maxRaw,
        double threshold)
    {
        if (candidate.SourceFile is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var sibling in bySource[candidate.SourceFile])
        {
            if (Math.Abs(sibling.ChunkIndex - candidate.ChunkIndex) == 1
                && sibling.Ranking >= maxRaw - threshold)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Drops a source's weak adjacent siblings (score gap ≥ threshold) into its best chunk's result.</summary>
    private static IReadOnlyList<MemorySearchResult> Consolidate(
        IReadOnlyList<MemorySearchResult> order,
        IReadOnlyDictionary<string, double> scores,
        double threshold)
    {
        if (order.Count == 0)
        {
            return order;
        }

        var bestBySource = new Dictionary<string, MemorySearchResult>(StringComparer.Ordinal);
        foreach (var result in order)
        {
            if (result.SourceFile is not null && !bestBySource.ContainsKey(result.SourceFile))
            {
                bestBySource[result.SourceFile] = result;
            }
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in order)
        {
            if (result.SourceFile is null
                || !bestBySource.TryGetValue(result.SourceFile, out var best)
                || ReferenceEquals(best, result)
                || Math.Abs(best.ChunkIndex - result.ChunkIndex) != 1)
            {
                continue;
            }

            if (scores[best.Hash] - scores[result.Hash] >= threshold)
            {
                merged.Add(result.Hash);
            }
        }

        return merged.Count == 0
            ? order
            : order.Where(result => !merged.Contains(result.Hash)).ToList();
    }
}
