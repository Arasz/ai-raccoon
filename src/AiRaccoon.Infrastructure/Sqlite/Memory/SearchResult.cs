using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public record SearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming);

public sealed record SearchResults
{
    public HashIndexes Indexes { get; } = HashIndexes.Create();
    public List<VectorSearchResult> Vector { get; } = [];
    public TimeSpan VectorTotalTiming => TimeSpan.FromMilliseconds(Vector.Sum(result => result.SearchTiming.TotalMilliseconds));
    public List<FtsSearchResult> Fts { get; } = [];
    public TimeSpan FtsTotalTiming => TimeSpan.FromMilliseconds(Fts.Sum(result => result.SearchTiming.TotalMilliseconds));

    public void AddResults(VectorSearchResult vector, FtsSearchResult fts)
    {
        Vector.Add(vector);
        Fts.Add(fts);
    }
}

public sealed record FtsSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming);

public sealed record VectorSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming);

public sealed record FusedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public required IReadOnlyList<MemorySearchResult> VectorCandidates { get; init; }
    public required IReadOnlyList<MemorySearchResult> FtsCandidates { get; init; }
}

public sealed record AdjustedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public FusionDiff? FusionDiff { get; init; }
}

public sealed record DeferredSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming)
{
    public FusionDiff? FusionDiff { get; init; }
}

public sealed record MergedSearchResult(IReadOnlyList<MemorySearchResult> Results, TimeSpan SearchTiming) : SearchResult(Results, SearchTiming);
