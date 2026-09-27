using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Catch-up scan (ADR-0121): a never-synced watch gets a full scan; otherwise each file is re-queued
///     when it has no fingerprint, its mtime is at or after its fingerprint's time, or its size differs.
///     Deletions from downtime are reconciled, and scans are single-flighted per (projectId, path).
/// </summary>
public sealed partial class WatchCatchUp(
    WatchPipeline pipeline,
    IWatchStore watchStore,
    WatchScanGuard scanGuard,
    IWatchScanLease scanLease,
    TimeProvider timeProvider,
    ILogger<WatchCatchUp> logger,
    IIgnoreRulesProvider ignoreRulesProvider,
    IndexableFileWalk walk) : IWatchScanInitiator
{
    /// <summary>A mid-scan ignore-file edit can only ever redo the walk this many times before the
    /// scan gives up trying to reach a stable read — defensive only; real edits settle in one.</summary>
    private const int MaxRescanAttempts = 5;

    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path, CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, false, ct), cancellationToken);

    /// <summary>IWatchScanInitiator: triggers a full re-scan (ignore-file edit) — single-flighted,
    /// like every other scan trigger; a scan already running is joined, not duplicated.</summary>
    void IWatchScanInitiator.EnqueueInitialScan(string projectId, string path) => EnqueueInitialScan(projectId, path);

    /// <summary>Restart scan of an already-synced watch: queues only the files changed since their own fingerprint.</summary>
    public void EnqueueChangedFiles(string projectId, string path, CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, true, ct), cancellationToken);

    /// <summary>Cancels every in-flight scan (host shutdown).</summary>
    public void CancelAllScans() => scanGuard.CancelAll();

    /// <summary>
    ///     Lists the files under path that are due: all of them when <paramref name="stamps" /> is null,
    ///     otherwise those with no fingerprint, an mtime at or after it, or a different size. Skips hidden
    ///     and denied directories and ignore-rule matches; a file target lists itself, a missing one nothing.
    /// </summary>
    internal static IEnumerable<string> EnumerateFiles(IndexableFileWalk walk, string path, IReadOnlyDictionary<string, WatchFileStamp>? stamps,
        IgnoreRules? ignoreRules = null)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path) && IsDue(path, stamps))
            {
                yield return path;
            }

            yield break;
        }

        var rules = ignoreRules ?? IgnoreRules.Empty;
        foreach (var file in walk.Under(path))
        {
            if (WatchDenySet.Excludes(path, file))
            {
                continue;
            }

            if (rules.HasRules && IsIgnoredFile(rules, path, file))
            {
                continue;
            }

            if (IsDue(file, stamps))
            {
                yield return file;
            }
        }
    }

    private static bool IsIgnoredFile(IgnoreRules rules, string root, string file) =>
        !string.Equals(Path.GetFileName(file), IgnoreRulesProvider.FileName, StringComparison.Ordinal) &&
        rules.IsIgnored(Path.GetRelativePath(root, file), false);

    /// <summary>
    ///     Whether the scan queues <paramref name="file" />. A fingerprinted file deleted since the walk
    ///     listed it is not due: the reconcile pass removes its fingerprint, and the scan carries on.
    /// </summary>
    internal static bool IsDue(string file, IReadOnlyDictionary<string, WatchFileStamp>? stamps)
    {
        if (stamps is null || !stamps.TryGetValue(IngestPath.Normalize(file), out var stamp))
        {
            return true;
        }

        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return false;
        }

        return new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds() >= stamp.UpdatedAt ||
               stamp.Size is { } size && size != info.Length;
    }

    private async Task ScanCoreAsync(string projectId, string path, bool changedOnly,
        CancellationToken cancellationToken)
    {
        if (!await scanLease.TryAcquireAsync(projectId, path, cancellationToken))
        {
            return;
        }

        var startedAt = timeProvider.GetUtcNow();
        try
        {
            for (var attempt = 0; attempt < MaxRescanAttempts; attempt++)
            {
                var ignoreRules = await ignoreRulesProvider.LoadAsync(path, cancellationToken);
                if (!await RunOnePassAsync(projectId, path, changedOnly, ignoreRules, cancellationToken))
                {
                    // Lease lost mid-pass — already logged and released by RunOnePassAsync's caller contract.
                    return;
                }

                // Re-read at the end of the pass: a mid-scan ignore-file edit must apply before the
                // scan chain settles, without any new WatchScanGuard queue state (pinned H10) — the
                // running scan simply redoes its own walk, full, with fresh rules.
                var reread = await ignoreRulesProvider.LoadAsync(path, cancellationToken);
                if (reread == ignoreRules)
                {
                    break;
                }

                changedOnly = false;
            }
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
            await scanLease.ReleaseAsync(projectId, path, CancellationToken.None);
        }
    }

    /// <summary>One walk + reconcile pass. Returns false when the lease was lost mid-pass (caller stops).</summary>
    private async Task<bool> RunOnePassAsync(string projectId, string path, bool changedOnly,
        IgnoreRules ignoreRules, CancellationToken cancellationToken)
    {
        var stamps = changedOnly ? await watchStore.ListFileStampsAsync(projectId, cancellationToken) : null;
        var nextRenew = timeProvider.GetUtcNow() + SqliteWatchScanLease.HeartbeatInterval;
        foreach (var file in EnumerateFiles(walk, path, stamps, ignoreRules))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Renew before enqueueing, never after: a lost lease must add nothing further.
            var now = timeProvider.GetUtcNow();
            if (now >= nextRenew)
            {
                if (!await scanLease.TryRenewAsync(projectId, path, cancellationToken))
                {
                    Log.ScanLeaseLost(logger, path);
                    return false;
                }

                nextRenew = timeProvider.GetUtcNow() + SqliteWatchScanLease.HeartbeatInterval;
            }

            pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Created));
        }

        await ReconcileAsync(projectId, path, ignoreRules, cancellationToken);
        return true;
    }

    /// <summary>
    ///     One walk of the fingerprinted files under the watch: a file missing on disk was deleted
    ///     while the server was down, and one the exclusion rule (#494) or an ignore rule now covers
    ///     was indexed before that rule applied. Either way, enqueue Deleted so the digest's own gate
    ///     removes the stale chunks and the fingerprint.
    /// </summary>
    private async Task ReconcileAsync(string projectId, string watchPath, IgnoreRules ignoreRules,
        CancellationToken cancellationToken)
    {
        foreach (var file in await watchStore.ListFilesAsync(projectId, cancellationToken))
        {
            if (!IngestPath.IsWithinScope(file, watchPath))
            {
                continue;
            }

            if (!File.Exists(file) || WatchDenySet.Excludes(watchPath, file) ||
                ignoreRules.HasRules && IsIgnoredFile(ignoreRules, watchPath, file))
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
