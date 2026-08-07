using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     D1 catch-up: a never-synced watch (watermark 0) gets a full initial scan; otherwise only
///     files with mtime strictly after the watermark are re-queued. Also reconciles deletions
///     that happened while the server was down (see docs/features/file-watcher/file-watcher.feature).
///     Scans are single-flighted per (projectId, path) via <see cref="WatchScanGuard"/> and
///     cancellable — removal (<see cref="WatchPipeline.UnregisterWatch"/>) and host shutdown both
///     stop an in-flight scan instead of letting it run to completion regardless.
/// </summary>
public sealed partial class WatchCatchUp(
    WatchPipeline pipeline,
    IWatchStore watchStore,
    WatchScanGuard scanGuard,
    IWatchScanLease scanLease,
    TimeProvider timeProvider,
    ILogger<WatchCatchUp> logger)
{
    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path, CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, null, ct), cancellationToken);

    public void EnqueueChangedSince(string projectId, string path, long watermark,
        CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, watermark, ct),
            cancellationToken);

    /// <summary>Cancels every in-flight scan (host shutdown).</summary>
    public void CancelAllScans() => scanGuard.CancelAll();

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

    private async Task ScanCoreAsync(string projectId, string path, long? sinceWatermark,
        CancellationToken cancellationToken)
    {
        if (!await scanLease.TryAcquireAsync(projectId, path, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var startedAt = timeProvider.GetUtcNow();
        try
        {
            var nextRenew = startedAt + SqliteWatchScanLease.HeartbeatInterval;
            foreach (var file in EnumerateFiles(path, sinceWatermark))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Renew before enqueueing, never after: a lost lease must add nothing further.
                var now = timeProvider.GetUtcNow();
                if (now >= nextRenew)
                {
                    if (!await scanLease.TryRenewAsync(projectId, path, cancellationToken).ConfigureAwait(false))
                    {
                        Log.ScanLeaseLost(logger, path);
                        return;
                    }

                    nextRenew = timeProvider.GetUtcNow() + SqliteWatchScanLease.HeartbeatInterval;
                }

                pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Created));
            }

            await ReconcileMissingAsync(projectId, path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.ScanCancelled(logger, path, timeProvider.GetUtcNow() - startedAt);
        }
        catch (Exception ex)
        {
            Log.ScanError(logger, path, ex);
        }
        finally
        {
            // Never with the scan's own (possibly cancelled) token: a cancelled release would
            // never happen, parking the lease for a full TTL on every removal.
            await scanLease.ReleaseAsync(projectId, path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Restart reconciliation: a fingerprinted file missing on disk is a delete that
    /// happened while the server was down — enqueue Deleted so its chunks are removed.</summary>
    private async Task ReconcileMissingAsync(string projectId, string watchPath, CancellationToken cancellationToken)
    {
        foreach (var file in await watchStore.ListFilesAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            if (IngestPath.IsWithinScope(file, watchPath) && !File.Exists(file))
            {
                pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Deleted));
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 310, Level = LogLevel.Error, Message = "Watch catch-up scan failed for {Path}")]
        public static partial void ScanError(ILogger logger, string path, Exception exception);

        [LoggerMessage(EventId = 311, Level = LogLevel.Information,
            Message = "Watch catch-up scan for {Path} cancelled after {Elapsed}")]
        public static partial void ScanCancelled(ILogger logger, string path, TimeSpan elapsed);

        [LoggerMessage(EventId = 312, Level = LogLevel.Warning,
            Message = "Watch catch-up scan for {Path} lost its lease to another process and stopped")]
        public static partial void ScanLeaseLost(ILogger logger, string path);
    }
}
