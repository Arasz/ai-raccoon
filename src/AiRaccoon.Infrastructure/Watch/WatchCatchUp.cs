using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     D1 catch-up: a never-synced watch (watermark 0) gets a full initial scan; otherwise only
///     files with mtime strictly after the watermark are re-queued. Also reconciles deletions
///     that happened while the server was down (see docs/features/file-watcher/file-watcher.feature).
/// </summary>
public sealed partial class WatchCatchUp(
    WatchPipeline pipeline,
    IWatchStore watchStore,
    ILogger<WatchCatchUp> logger)
{
    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path) =>
        LastScan = Task.Run(() => ScanCoreAsync(projectId, path, sinceWatermark: null));

    public void EnqueueChangedSince(string projectId, string path, long watermark) =>
        LastScan = Task.Run(() => ScanCoreAsync(projectId, path, sinceWatermark: watermark));

    /// <summary>Deterministic core: files under path, optionally filtered by mtime &gt; watermark.
    /// A watched FILE target enumerates itself; a missing target enumerates nothing (catch-up
    /// reconciliation removes its stale chunks).</summary>
    internal static IEnumerable<string> EnumerateFiles(string path, long? sinceWatermark)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path) &&
                (sinceWatermark is null ||
                 new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds() > sinceWatermark.Value))
            {
                yield return path;
            }

            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (sinceWatermark is null ||
                new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() > sinceWatermark.Value)
            {
                yield return file;
            }
        }
    }

    private async Task ScanCoreAsync(string projectId, string path, long? sinceWatermark)
    {
        try
        {
            foreach (var file in EnumerateFiles(path, sinceWatermark))
            {
                pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Created));
            }

            await ReconcileMissingAsync(projectId, path);
        }
        catch (Exception ex)
        {
            Log.ScanError(logger, path, ex);
        }
    }

    /// <summary>Restart reconciliation: a fingerprinted file missing on disk is a delete that
    /// happened while the server was down — enqueue Deleted so its chunks are removed.</summary>
    private async Task ReconcileMissingAsync(string projectId, string watchPath)
    {
        foreach (var file in await watchStore.ListFilesAsync(projectId))
        {
            if (WatchPath.IsWithinScope(file, watchPath) && !File.Exists(file))
            {
                pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Deleted));
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 310, Level = LogLevel.Error, Message = "Watch catch-up scan failed for {Path}")]
        public static partial void ScanError(ILogger logger, string path, Exception exception);
    }
}
