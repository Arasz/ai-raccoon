using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     IWatchService impl: add/remove/status with enable + scope + existence validation, per-
///     (projectId, path) idempotency and normalized identity (D3); the pipeline owns runtime status.
/// </summary>
public sealed class WatchService(IWatchStore store, IMemoryStore memory, WatchPipeline pipeline, TimeProvider timeProvider)
    : IWatchService
{
    public async Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        var config = await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!config.Enabled)
        {
            throw new WatchDisabledException(projectId);
        }

        var normalized = WatchPath.Normalize(path);
        if (!config.Scope.Any(entry => WatchPath.IsWithinScope(normalized, entry)))
        {
            throw new PathOutsideScopeException(normalized);
        }

        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            throw new PathNotFound(normalized);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        // lastChangeTs 0 = never synced: catch-up treats a fresh watch as a full initial scan (D1).
        await store.AddWatchAsync(projectId, normalized, now, lastChangeTs: 0, cancellationToken)
            .ConfigureAwait(false);
        pipeline.RegisterWatch(projectId, normalized);
    }

    public async Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        var normalized = WatchPath.Normalize(path);
        await store.RemoveWatchAsync(projectId, normalized, cancellationToken).ConfigureAwait(false);
        pipeline.UnregisterWatch(projectId, normalized);
    }

    public async Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        var registrations = (await store.ListWatchesAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Path, WatchPath.PathComparer)
            .ToArray();
        var runtime = pipeline.GetStatuses(projectId).ToDictionary(s => s.Path, WatchPath.PathComparer);

        var result = new List<WatchStatus>(registrations.Length);
        foreach (var registration in registrations)
        {
            result.Add(runtime.TryGetValue(registration.Path, out var status)
                ? status
                : new WatchStatus(projectId, registration.Path, WatchState.Scanning));
        }

        return result;
    }

    public async Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) =>
        (await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false)).Enabled;

    public async Task<bool> IsPathAllowedAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        var config = await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        var normalized = WatchPath.Normalize(path);
        return config.Scope.Any(entry => WatchPath.IsWithinScope(normalized, entry));
    }

    private async Task<WatchConfig> ResolveConfigAsync(string projectId, CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            WatchConfigKeys.EnabledProject(projectId), WatchConfigKeys.EnabledGlobal,
            WatchConfigKeys.ScopeProject(projectId), WatchConfigKeys.ScopeGlobal,
            WatchConfigKeys.ConcurrencyProject(projectId), WatchConfigKeys.ConcurrencyGlobal
        };
        var values = new Dictionary<string, string?>();
        foreach (var key in keys)
        {
            values[key] = await memory.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return WatchConfig.Resolve(projectId, key => values.GetValueOrDefault(key));
    }
}
