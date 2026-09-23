using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public record SearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming);

public sealed record SearchResults
{
    public ByHashIndex Index { get; } = ByHashIndex.Create();
    public List<VectorSearchResult> Vector { get; } = [];
    public TimeSpan VectorTotalTiming => TimeSpan.FromMilliseconds(Vector.Sum(result => result.SearchTiming.TotalMilliseconds));
    public List<FtsSearchResult> Fts { get; } = [];
    public TimeSpan FtsTotalTiming => TimeSpan.FromMilliseconds(Fts.Sum(result => result.SearchTiming.TotalMilliseconds));

    /// <summary>Hashes the keyword leg matched on every query term, across all searched contexts.</summary>
    public IReadOnlySet<string> AllTermsMatched =>
        Fts.SelectMany(result => result.AllTermsMatched).ToHashSet(StringComparer.Ordinal);

    public void AddResults(VectorSearchResult vector, FtsSearchResult fts)
    {
        Vector.Add(vector);
        Fts.Add(fts);
    }
}

public sealed record FtsSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    /// <summary>Hashes the conjunctive expression matched, kept even when the OR fallback replaced <see cref="SearchResult.Results" />.</summary>
    public IReadOnlyList<string> AllTermsMatched { get; init; } = [];
}

public sealed record VectorSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming);

public sealed record FusedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public required IReadOnlyList<MemorySearchResult> VectorCandidates { get; init; }
    public required IReadOnlyList<MemorySearchResult> FtsCandidates { get; init; }
    public IReadOnlyDictionary<string, RetrievalEvidence>? EvidenceByHash { get; init; }
    public FusionStats? Stats { get; init; }
}

public sealed record AdjustedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public FusionDiff? FusionDiff { get; init; }
    public IReadOnlyDictionary<string, RetrievalEvidence>? EvidenceByHash { get; init; }
    public FusionStats? Stats { get; init; }
    public int DroppedByFloor { get; init; }
}

public sealed record DeferredSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public static readonly DeferredSearchResult Empty = new([], TimeSpan.Zero)
    {
        FusionDiff = null
    };

    public required FusionDiff? FusionDiff { get; init; }
    public IReadOnlyDictionary<string, RetrievalEvidence>? EvidenceByHash { get; init; }
    public FusionStats? Stats { get; init; }
    public int DroppedByFloor { get; init; }
}

public sealed record MergedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public int DroppedByFloor { get; init; }
}
