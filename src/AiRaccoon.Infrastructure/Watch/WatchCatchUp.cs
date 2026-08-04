using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     D1 catch-up: a never-synced watch (watermark 0) gets a full initial scan; otherwise only
///     files with mtime strictly after the watermark are re-queued. Scans run in the background
///     so memory_watch_add returns immediately (feature rule 4); the watermark advances as
///     digests complete (executor side).
/// </summary>
public sealed class WatchCatchUp(WatchPipeline pipeline, ILogger<WatchCatchUp> logger)
{
    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path)
    {
        _ = (pipeline, logger, projectId, path);
        throw new NotImplementedException();
    }

    public void EnqueueChangedSince(string projectId, string path, long watermark)
    {
        _ = (pipeline, logger, projectId, path, watermark);
        throw new NotImplementedException();
    }

    /// <summary>Deterministic core: files under path, optionally filtered by mtime &gt; watermark.</summary>
    internal static IEnumerable<string> EnumerateFiles(string path, long? sinceWatermark)
    {
        _ = (path, sinceWatermark);
        throw new NotImplementedException();
    }
}
