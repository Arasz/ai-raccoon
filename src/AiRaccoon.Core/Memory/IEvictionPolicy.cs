namespace AiRaccoon.Core.Memory;

/// <summary>
///     The eviction strategy: which project loses a queued row when the propose tier is over
///     capacity. The store executes the victim-row query (lowest score, oldest first) within
///     the chosen project.
/// </summary>
public interface IEvictionPolicy
{
    /// <summary>Project with the greatest queued count; null when the queue is empty. Tie: ordinal-smallest id.</summary>
    string? EvictionTarget(IReadOnlyDictionary<string, int> perProjectCounts);
}

/// <summary>Uniform fair-share rule: the biggest occupier loses its weakest row. If that is the inserter, so be it.
/// This is why ADR-0007's per-project reservation (cap/n) is never violated without a separate check: eviction
/// only runs when the total is over cap, so by pigeonhole the greatest count must already exceed cap/n.</summary>
public sealed class UniformCountEvictionPolicy : IEvictionPolicy
{
    public string? EvictionTarget(IReadOnlyDictionary<string, int> perProjectCounts)
    {
        if (perProjectCounts.Count == 0)
        {
            return null;
        }

        return perProjectCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First().Key;
    }
}
