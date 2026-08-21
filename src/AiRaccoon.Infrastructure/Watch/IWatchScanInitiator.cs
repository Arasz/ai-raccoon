namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Triggers a full re-scan of a watch (ignore-file edit) without the caller depending on
///     <see cref="WatchCatchUp" /> or <see cref="WatchPipeline" /> directly — breaks the
///     WatchPipeline → executor → pipeline back-reference cycle
///     (docs/work/2026-08-21-code-search-implementation-plan.md §5.3). Implemented by
///     <see cref="WatchCatchUp" />; injected into the digest executor as
///     <see cref="Lazy{T}" /> so its own construction never eagerly resolves
///     <see cref="WatchCatchUp" /> (which itself needs the pipeline, which needs the executor).
/// </summary>
public interface IWatchScanInitiator
{
    void EnqueueInitialScan(string projectId, string path);
}
