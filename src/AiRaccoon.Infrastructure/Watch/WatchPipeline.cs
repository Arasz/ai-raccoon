using System.Threading.Channels;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Runtime (non-persisted) status of one registered watch.</summary>
internal sealed record WatchRuntimeState(WatchState State, string? LastError, DateTimeOffset? LastSync);

public interface IWatchPipeline
{
    /// <summary>
    ///     Raised synchronously from <see cref="WatchPipeline.UnregisterWatch" /> — the one removal choke point
    ///     (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-1) every removal inherits, so callers with
    ///     their own liveness bookkeeping can invalidate it at the same instant instead of waiting for a poll.
    /// </summary>
    event Action<string, string>? Unregistered;

    /// <summary>Thread-safe entry point for the event source (docs/plans/file-watcher-implementation.md S5): writes into the single channel.</summary>
    void Enqueue(WatchEvent evt);

    /// <summary>Registers a watch's runtime state (Scanning); idempotent — a re-add never resets state.</summary>
    void RegisterWatch(string projectId, string path);

    /// <summary>
    ///     Drops a watch's runtime state and any pending digests under it (remove must not resurrect),
    ///     and cancels its in-flight catch-up scan (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-1).
    /// </summary>
    void UnregisterWatch(string projectId, string path);

    /// <summary>Marks a watch scanning (docs/plans/file-watcher-implementation.md S5's initial scan) — clears the last error.</summary>
    void MarkScanning(string projectId, string path);

    IReadOnlyList<WatchStatus> GetStatuses(string projectId);

    /// <summary>Loop: tick every second until cancelled; a failing iteration never kills the loop.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Mirror pipeline core: one channel of filesystem events, per-path pending aggregation,
///     a 1s tick (injected TimeProvider), drain to the scheduler. Every iteration is contained —
///     errors land in status, nothing escapes the MCP server (docs/features/file-watcher/file-watcher.feature rule 13).
/// </summary>
public sealed partial class WatchPipeline(
    IWatchScheduler scheduler,
    IWatchDigestExecutor executor,
    IWatchRetryPolicy retryPolicy,
    IWatchScanGuard scanGuard,
    IMemoryStore memoryStore,
    TimeProvider timeProvider,
    ILogger<WatchPipeline> logger) : IWatchPipeline
{
    private readonly Channel<WatchEvent> _events = Channel.CreateUnbounded<WatchEvent>();
    private readonly Lock _gate = new();
    private readonly Dictionary<(string ProjectId, string Path), WatchEvent> _pending = new(WatchKeyComparer.Instance);
    private readonly Dictionary<(string ProjectId, string Path), WatchRuntimeState> _runtime = new(WatchKeyComparer.Instance);
    private readonly Dictionary<string, List<string>> _watchPathsByProject = new(StringComparer.Ordinal);
    public static TimeSpan TickInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Raised synchronously from <see cref="UnregisterWatch" /> — the one removal choke point
    ///     (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-1) every removal inherits, so callers with
    ///     their own liveness bookkeeping can invalidate it at the same instant instead of waiting for a poll.
    /// </summary>
    public event Action<string, string>? Unregistered;

    /// <summary>Thread-safe entry point for the event source (docs/plans/file-watcher-implementation.md S5): writes into the single channel.</summary>
    public void Enqueue(WatchEvent evt)
    {
        var normalized = evt with
        {
            Path = IngestPath.Normalize(evt.Path),
            OldPath = evt.OldPath is null ? null : IngestPath.Normalize(evt.OldPath)
        };
        _events.Writer.TryWrite(normalized);
    }

    /// <summary>Registers a watch's runtime state (Scanning); idempotent — a re-add never resets state.</summary>
    public void RegisterWatch(string projectId, string path)
    {
        var normalized = IngestPath.Normalize(path);
        lock (_gate)
        {
            if (_runtime.TryAdd((projectId, normalized), new WatchRuntimeState(WatchState.Scanning, null, null)))
            {
                if (!_watchPathsByProject.TryGetValue(projectId, out var watches))
                {
                    watches = [];
                    _watchPathsByProject[projectId] = watches;
                }

                watches.Add(normalized);
            }
        }
    }

    /// <summary>
    ///     Drops a watch's runtime state and any pending digests under it (remove must not resurrect),
    ///     and cancels its in-flight catch-up scan (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-1).
    /// </summary>
    public void UnregisterWatch(string projectId, string path)
    {
        var normalized = IngestPath.Normalize(path);
        lock (_gate)
        {
            _runtime.Remove((projectId, normalized));
            if (_watchPathsByProject.TryGetValue(projectId, out var watches))
            {
                watches.Remove(normalized);
            }

            foreach (var key in _pending.Keys
                         .Where(k => k.ProjectId == projectId && IngestPath.IsWithinScope(k.Path, normalized))
                         .ToArray())
            {
                _pending.Remove(key);
            }
        }

        retryPolicy.Forget(projectId, normalized);
        scanGuard.Cancel(projectId, normalized);
        Unregistered?.Invoke(projectId, normalized);
    }

    /// <summary>Marks a watch scanning (docs/plans/file-watcher-implementation.md S5's initial scan) — clears the last error.</summary>
    public void MarkScanning(string projectId, string path)
    {
        var normalized = IngestPath.Normalize(path);
        lock (_gate)
        {
            if (_runtime.TryGetValue((projectId, normalized), out var current))
            {
                _runtime[(projectId, normalized)] = current with { State = WatchState.Scanning, LastError = null };
            }
        }
    }

    public IReadOnlyList<WatchStatus> GetStatuses(string projectId)
    {
        lock (_gate)
        {
            return
            [
                .. _runtime
                    .Where(kv => kv.Key.ProjectId == projectId)
                    .Select(kv => new WatchStatus(projectId, kv.Key.Path, kv.Value.State, kv.Value.LastError,
                        kv.Value.LastSync))
                    .OrderBy(s => s.Path, IngestPath.PathComparer)
            ];
        }
    }

    /// <summary>Loop: tick every second until cancelled; a failing iteration never kills the loop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                await TickOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.PipelineLoopError(logger, ex);
            }
        }
    }

    /// <summary>One tick: fold channel events into pending, drain due paths to the scheduler, await the batch.</summary>
    internal async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        while (_events.Reader.TryRead(out var evt))
        {
            lock (_gate)
            {
                _pending[(evt.ProjectId, evt.Path)] = evt;
            }
        }

        var now = timeProvider.GetUtcNow();
        var jobs = new List<WatchJob>();
        lock (_gate)
        {
            foreach (var (key, evt) in _pending)
            {
                var watchPath = FindContainingWatch(key.ProjectId, evt.Path);
                if (watchPath is null)
                {
                    _pending.Remove(key);
                    continue;
                }

                if (!retryPolicy.ShouldAttempt(key.ProjectId, watchPath, now))
                {
                    if (retryPolicy.IsStopped(key.ProjectId, watchPath))
                    {
                        _pending.Remove(key);
                    }

                    continue;
                }

                _pending.Remove(key);
                jobs.Add(new WatchJob(evt, watchPath));
            }
        }

        if (jobs.Count == 0)
        {
            return;
        }

        var concurrency = await ResolveConcurrencyAsync(jobs, cancellationToken).ConfigureAwait(false);
        await scheduler.RunBatchAsync(jobs, concurrency, RunJobAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunJobAsync(WatchJob job, CancellationToken cancellationToken)
    {
        var evt = job.Event;
        try
        {
            await executor.DigestAsync(evt.ProjectId, job.WatchPath, evt.Path, evt.Kind, evt.OldPath,
                    cancellationToken)
                .ConfigureAwait(false);
            retryPolicy.RecordSuccess(evt.ProjectId, job.WatchPath);
            lock (_gate)
            {
                // A job dispatched before UnregisterWatch removed the entry must not resurrect it
                // (D21): only update state for a watch that is still registered.
                if (_runtime.ContainsKey((evt.ProjectId, job.WatchPath)))
                {
                    _runtime[(evt.ProjectId, job.WatchPath)] =
                        new WatchRuntimeState(WatchState.Healthy, null, timeProvider.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var state = retryPolicy.RecordFailure(evt.ProjectId, job.WatchPath, timeProvider.GetUtcNow());
            lock (_gate)
            {
                // Same rule on the failure path: a removed watch stays removed.
                if (_runtime.TryGetValue((evt.ProjectId, job.WatchPath), out var current))
                {
                    _runtime[(evt.ProjectId, job.WatchPath)] = current with { State = state, LastError = ex.Message };
                }
            }
        }
    }

    /// <summary>Most specific registered watch containing the file (deterministic digest ownership).</summary>
    private string? FindContainingWatch(string projectId, string filePath)
    {
        if (!_watchPathsByProject.TryGetValue(projectId, out var watches))
        {
            return null;
        }

        string? best = null;
        foreach (var watch in watches)
        {
            if (IngestPath.IsWithinScope(filePath, watch) && (best is null || watch.Length > best.Length))
            {
                best = watch;
            }
        }

        return best;
    }

    private async Task<Dictionary<string, int>> ResolveConcurrencyAsync(IReadOnlyList<WatchJob> jobs,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>();
        foreach (var projectId in jobs.Select(j => j.Event.ProjectId).Distinct())
        {
            var settings = new Dictionary<string, string?>
            {
                [WatchConfigKeys.ConcurrencyProject(projectId)] =
                    await memoryStore.GetSettingAsync(WatchConfigKeys.ConcurrencyProject(projectId),
                            cancellationToken)
                        .ConfigureAwait(false),
                [WatchConfigKeys.ConcurrencyGlobal] =
                    await memoryStore.GetSettingAsync(WatchConfigKeys.ConcurrencyGlobal, cancellationToken)
                        .ConfigureAwait(false)
            };
            result[projectId] = WatchConfig.Resolve(projectId, key => settings.GetValueOrDefault(key)).Concurrency;
        }

        return result;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 302, Level = LogLevel.Error, Message = "Watch pipeline loop iteration failed")]
        public static partial void PipelineLoopError(ILogger logger, Exception exception);
    }
}
