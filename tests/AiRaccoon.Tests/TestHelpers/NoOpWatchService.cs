using AiRaccoon.Core.Watch;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>No-op <see cref="IWatchService"/> for tests that need a watch service present but never
/// assert on what it did — every path is allowed and every tier reports enabled.</summary>
public sealed class NoOpWatchService : IWatchService
{
    public Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WatchStatus>>([]);

    public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> IsPathAllowedAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
