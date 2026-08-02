namespace AiRaccon.Core.Memory;

/// <summary>Port over the sqlite-memory SQL surface; thin and SQL-shaped, implemented by the Infrastructure layer.</summary>
public interface IMemoryStore
{
    Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default);

    Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default);

    Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default);
}
