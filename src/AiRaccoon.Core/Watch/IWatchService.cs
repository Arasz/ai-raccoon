namespace AiRaccoon.Core.Watch;

/// <summary>
///     Watch service port (S2): the surface the MCP tools call and the pipeline implements.
///     Core types only — infrastructure-free.
/// </summary>
public interface IWatchService
{
    Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId, CancellationToken cancellationToken = default);

    Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default);

    Task<bool> IsPathAllowedAsync(string projectId, string path, CancellationToken cancellationToken = default);
}
