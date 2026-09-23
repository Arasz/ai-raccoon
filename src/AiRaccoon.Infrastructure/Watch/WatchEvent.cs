namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Filesystem change kinds entering the pipeline (event source, docs/plans/file-watcher-implementation.md S5).</summary>
public enum WatchEventKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

/// <summary>One filesystem change entering the pipeline; paths are normalized per docs/plans/file-watcher-implementation.md D3 before use.</summary>
public sealed record WatchEvent(string ProjectId, string Path, WatchEventKind Kind, string? OldPath = null);

/// <summary>A pending digest job: the event plus the registered watch it belongs to.</summary>
public sealed record WatchJob(WatchEvent Event, string WatchPath);
