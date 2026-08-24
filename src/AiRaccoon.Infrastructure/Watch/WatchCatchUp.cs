using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Catch-up scan (docs/plans/file-watcher-implementation.md D1): a never-synced watch gets a
///     full initial scan; otherwise it re-queues files changed since the watermark or never
///     fingerprinted, reconciles deletions from downtime, and is single-flighted per (projectId, path).
///     Enumeration also skips hidden directory segments, the built-in deny set
///     (<see cref="WatchDenySet.Excludes" />, the same predicate the digest applies to events), and
///     `ai-raccoon.ignore` matches (docs/work/2026-08-21-code-search-implementation-plan.md §2.1/
///     §2.3); <see cref="ReconcileAsync" /> cleans fingerprinted-but-now-excluded paths, and a
///     mid-scan edit to the ignore file is caught by re-comparing the loaded <see cref="IgnoreRules" />
///     for value equality (its source text, not a filesystem mtime check) at the end of each pass —
///     no new queue state on <see cref="WatchScanGuard" /> (pinned H10, which named mtime-recheck;
///     this is the shape actually built, and both are §2.1-permitted).
/// </summary>
public sealed partial class WatchCatchUp(
    WatchPipeline pipeline,
    IWatchStore watchStore,
    WatchScanGuard scanGuard,
    IWatchScanLease scanLease,
    TimeProvider timeProvider,
    ILogger<WatchCatchUp> logger,
    IIgnoreRulesProvider ignoreRulesProvider) : IWatchScanInitiator
{
    /// <summary>A mid-scan ignore-file edit can only ever redo the walk this many times before the
    /// scan gives up trying to reach a stable read — defensive only; real edits settle in one.</summary>
    private const int MaxRescanAttempts = 5;

    /// <summary>Task of the most recently enqueued scan (tests await it for determinism).</summary>
    internal Task? LastScan { get; private set; }

    public void EnqueueInitialScan(string projectId, string path, CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, null, ct), cancellationToken);

    /// <summary>IWatchScanInitiator: triggers a full re-scan (ignore-file edit) — single-flighted,
    /// like every other scan trigger; a scan already running is joined, not duplicated.</summary>
    void IWatchScanInitiator.EnqueueInitialScan(string projectId, string path) => EnqueueInitialScan(projectId, path);

    public void EnqueueChangedSince(string projectId, string path, long watermark,
        CancellationToken cancellationToken = default) =>
        LastScan = scanGuard.Run(projectId, path, ct => ScanCoreAsync(projectId, path, watermark, ct),
            cancellationToken);

    /// <summary>Cancels every in-flight scan (host shutdown).</summary>
    public void CancelAllScans() => scanGuard.CancelAll();

    /// <summary>
    ///     Deterministic core: files under path are due when there is no watermark, the mtime is
    ///     after the watermark, or the file was never fingerprinted. A watched FILE target enumerates
    ///     itself (no ignore rules — no tree, §5.6); a missing target enumerates nothing
    ///     (reconciliation removes its stale chunks). Directory enumeration skips hidden segments,
    ///     the built-in deny set, and any path the ignore rules match.
    /// </summary>
    internal static IEnumerable<string> EnumerateFiles(string path, long? sinceWatermark,
        IReadOnlySet<string> fingerprinted, IgnoreRules? ignoreRules = null)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path) && IsDue(path, sinceWatermark, fingerprinted))
            {
                yield return path;
            }

            yield break;
        }

        var rules = ignoreRules ?? IgnoreRules.Empty;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            if (WatchDenySet.Excludes(path, file))
            {
                continue;
            }

            if (rules.HasRules && IsIgnoredFile(rules, path, file))
            {
                continue;
            }

            if (IsDue(file, sinceWatermark, fingerprinted))
            {
                yield return file;
            }
        }
    }

    private static bool IsIgnoredFile(IgnoreRules rules, string root, string file) =>
        !string.Equals(Path.GetFileName(file), IgnoreRulesProvider.FileName, StringComparison.Ordinal) &&
        rules.IsIgnored(Path.GetRelativePath(root, file), false);

    private static bool IsDue(string file, long? sinceWatermark, IReadOnlySet<string> fingerprinted) =>
        sinceWatermark is null ||
        new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() > sinceWatermark.Value ||
        !fingerprinted.Contains(IngestPath.Normalize(file));

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
            var watermark = sinceWatermark;
            for (var attempt = 0; attempt < MaxRescanAttempts; attempt++)
            {
                var ignoreRules = await ignoreRulesProvider.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (!await RunOnePassAsync(projectId, path, watermark, ignoreRules, cancellationToken)
                        .ConfigureAwait(false))
                {
                    // Lease lost mid-pass — already logged and released by RunOnePassAsync's caller contract.
                    return;
                }

                // Re-read at the end of the pass: a mid-scan ignore-file edit must apply before the
                // scan chain settles, without any new WatchScanGuard queue state (pinned H10) — the
                // running scan simply redoes its own walk, full (watermark null), with fresh rules.
                var reread = await ignoreRulesProvider.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (reread == ignoreRules)
                {
                    break;
                }

                watermark = null;
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
            await scanLease.ReleaseAsync(projectId, path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One walk + reconcile pass. Returns false when the lease was lost mid-pass (caller stops).</summary>
    private async Task<bool> RunOnePassAsync(string projectId, string path, long? sinceWatermark,
        IgnoreRules ignoreRules, CancellationToken cancellationToken)
    {
        var fingerprinted = sinceWatermark is null
            ? (IReadOnlySet<string>)new HashSet<string>()
            : (await watchStore.ListFilesAsync(projectId, cancellationToken).ConfigureAwait(false))
            .ToHashSet(IngestPath.PathComparer);
        var nextRenew = timeProvider.GetUtcNow() + SqliteWatchScanLease.HeartbeatInterval;
        foreach (var file in EnumerateFiles(path, sinceWatermark, fingerprinted, ignoreRules))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Renew before enqueueing, never after: a lost lease must add nothing further.
            var now = timeProvider.GetUtcNow();
            if (now >= nextRenew)
            {
                if (!await scanLease.TryRenewAsync(projectId, path, cancellationToken).ConfigureAwait(false))
                {
                    Log.ScanLeaseLost(logger, path);
                    return false;
                }

                nextRenew = timeProvider.GetUtcNow() + SqliteWatchScanLease.HeartbeatInterval;
            }

            pipeline.Enqueue(new WatchEvent(projectId, file, WatchEventKind.Created));
        }

        await ReconcileAsync(projectId, path, ignoreRules, cancellationToken).ConfigureAwait(false);
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
        foreach (var file in await watchStore.ListFilesAsync(projectId, cancellationToken).ConfigureAwait(false))
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
