using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     D1 catch-up: a never-synced watch (watermark 0) gets a full initial scan; otherwise only
///     files with mtime strictly after the watermark are re-queued. Scans run in the background
///     so memory_watch_add returns immediately (feature rule 4); the watermark advances as
///     digests complete (executor side).
/// </summary>
public sealed partial class WatchCatchUp(WatchPipeline pipeline, ILogger<WatchCatchUp> logger)
{
    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path) =>
        LastScan = Task.Run(() => ScanCoreAsync(projectId, path, sinceWatermark: null));

    public void EnqueueChangedSince(string projectId, string path, long watermark) =>
        LastScan = Task.Run(() => ScanCoreAsync(projectId, path, sinceWatermark: watermark));

    /// <summary>Deterministic core: files under path, optionally filtered by mtime &gt; watermark.</summary>
    internal static IEnumerable<string> EnumerateFiles(string path, long? sinceWatermark)
    {
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
        }
        catch (Exception ex)
        {
            Log.ScanError(logger, path, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 310, Level = LogLevel.Error, Message = "Watch catch-up scan failed for {Path}")]
        public static partial void ScanError(ILogger logger, string path, Exception exception);
    }
}
