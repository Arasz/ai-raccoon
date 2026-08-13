using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Per-(projectId, path) scan single-flight: a <see cref="Run" /> call for a watch already
///     scanning joins the in-flight scan instead of starting a second one; <see cref="Cancel" />
///     (fed by <see cref="WatchPipeline.UnregisterWatch" />) cancels only that watch's scan.
/// </summary>
public sealed class WatchScanGuard : IWatchScanGuard
{
    private readonly Dictionary<(string ProjectId, string Path), Entry> _entries = new(WatchKeyComparer.Instance);
    private readonly Lock _gate = new();

    public int StartedScans { get; private set; }

    public int SkippedScans { get; private set; }

    /// <summary>
    ///     Starts <paramref name="scan" /> for the watch unless one is already running, in which
    ///     case the caller joins the running scan's task. The scan always runs off-thread (async).
    /// </summary>
    public Task Run(string projectId, string path, Func<CancellationToken, Task> scan,
        CancellationToken cancellationToken = default)
    {
        var key = (projectId, IngestPath.Normalize(path));
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var running))
            {
                SkippedScans++;
                return running.Completion.Task;
            }

            entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _entries[key] = entry;
            StartedScans++;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await scan(entry.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (entry.Cts.IsCancellationRequested)
            {
                Complete(entry, key, null, true);
                return;
            }
            catch (Exception ex)
            {
                Complete(entry, key, ex, false);
                return;
            }

            Complete(entry, key, null, false);
        });

        return entry.Completion.Task;
    }

    /// <summary>Cancels the in-flight scan for one watch, if any; other watches are unaffected.</summary>
    public void Cancel(string projectId, string path)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue((projectId, IngestPath.Normalize(path)), out entry);
        }

        entry?.Cts.Cancel();
    }

    /// <summary>Cancels every in-flight scan (host shutdown).</summary>
    public void CancelAll()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Values];
        }

        foreach (var entry in entries)
        {
            entry.Cts.Cancel();
        }
    }

    public bool IsRunning(string projectId, string path)
    {
        lock (_gate)
        {
            return _entries.ContainsKey((projectId, IngestPath.Normalize(path)));
        }
    }

    public void Dispose() => CancelAll();

    /// <summary>
    ///     Removes the entry (if it is still the one this run inserted), disposes its CTS,
    ///     and completes the joined callers' task — runs on every exit path so a watch is never
    ///     left permanently unscannable (a leaked entry has no TTL to rescue it).
    /// </summary>
    private void Complete(Entry entry, (string ProjectId, string Path) key, Exception? faulted, bool cancelled)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
            }
        }

        entry.Cts.Dispose();

        if (cancelled)
        {
            entry.Completion.TrySetCanceled();
        }
        else if (faulted is not null)
        {
            entry.Completion.TrySetException(faulted);
        }
        else
        {
            entry.Completion.TrySetResult();
        }
    }

    private sealed class Entry(CancellationTokenSource cts, TaskCompletionSource completion)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public TaskCompletionSource Completion { get; } = completion;
    }
}
