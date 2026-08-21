using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     IWatchService impl: add/remove/status with enable + scope + existence validation, per-
///     (projectId, path) idempotency and normalized identity (docs/plans/file-watcher-implementation.md D3);
///     the pipeline owns runtime status. No-overlapping-watches
///     (docs/work/2026-08-21-code-search-implementation-plan.md §2.2/§5.5): reject-if-contained
///     before prune+register, both resolved through the shared <see cref="IWatchOverlapResolver" />
///     (the same instance the v11 ladder migration uses).
/// </summary>
public sealed class WatchService(
    IWatchStore store,
    IMemoryStore memory,
    WatchPipeline pipeline,
    TimeProvider timeProvider,
    IWatchOverlapResolver overlapResolver) : IWatchService
{
    public async Task<WatchAddOutcome> AddAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        var config = await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!config.Enabled)
        {
            throw new WatchDisabledException(projectId);
        }

        var normalized = IngestPath.Normalize(path);
        if (!config.Scope.Any(entry => IngestPath.IsWithinScope(normalized, entry)))
        {
            throw new PathOutsideScopeException(normalized);
        }

        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            throw new PathNotFoundException(normalized);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        // lastChangeTs 0 = never synced: catch-up treats a fresh watch as a full initial scan
        // (docs/plans/file-watcher-implementation.md D1). Resolve + prune + register all happen
        // inside ONE BEGIN IMMEDIATE transaction in the store (codereviewer MUST-FIX 7 + TOCTOU
        // close) — the read that decides the outcome and the write that commits it share the same
        // lock, so two concurrent AddAsync calls can never both decide "I win" against a stale
        // pre-transaction snapshot.
        var decision = await store.ResolveAndAddAsync(projectId, new WatchOverlapCandidate(normalized, now),
            overlapResolver, cancellationToken).ConfigureAwait(false);

        switch (decision.Outcome)
        {
            case WatchOverlapOutcome.Idempotent:
                // Exact literal-path re-add: idempotent no-op — still ensure the pipeline's
                // runtime state exists (RegisterWatch is itself idempotent).
                pipeline.RegisterWatch(projectId, normalized);
                return new WatchAddOutcome([], decision.CoveringPath);

            case WatchOverlapOutcome.Rejected:
                throw new WatchOverlapException(normalized, decision.CoveringPath!);

            default:
                var prunedPaths = decision.Pruned.Select(p => p.Path).ToArray();
                // Runtime state updates only after the transaction commits (idempotent; a crash
                // between commit and here leaves stale runtime state the hosted service's
                // registration poll reconciles).
                foreach (var prunedPath in prunedPaths)
                {
                    pipeline.UnregisterWatch(projectId, prunedPath);
                }

                pipeline.RegisterWatch(projectId, normalized);
                return new WatchAddOutcome(prunedPaths, null);
        }
    }

    public async Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        var normalized = IngestPath.Normalize(path);
        await store.RemoveWatchAsync(projectId, normalized, cancellationToken).ConfigureAwait(false);
        pipeline.UnregisterWatch(projectId, normalized);
    }

    public async Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        var registrations = (await store.ListWatchesAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Path, IngestPath.PathComparer)
            .ToArray();
        var runtime = pipeline.GetStatuses(projectId).ToDictionary(s => s.Path, IngestPath.PathComparer);

        var result = new List<WatchStatus>(registrations.Length);
        foreach (var registration in registrations)
        {
            result.Add(runtime.TryGetValue(registration.Path, out var status)
                ? status
                : new WatchStatus(projectId, registration.Path, WatchState.Scanning));
        }

        return result;
    }

    public async Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) => (await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false)).Enabled;

    public async Task<bool> IsPathAllowedAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        var config = await ResolveConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        var normalized = IngestPath.Normalize(path);
        return config.Scope.Any(entry => IngestPath.IsWithinScope(normalized, entry));
    }

    private async Task<WatchConfig> ResolveConfigAsync(string projectId, CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            WatchConfigKeys.EnabledProject(projectId), WatchConfigKeys.EnabledGlobal,
            IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.ScopeGlobal,
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
