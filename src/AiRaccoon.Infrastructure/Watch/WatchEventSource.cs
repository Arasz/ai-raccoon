using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Synthetic error event from the adapter (adapter failures never throw outward).</summary>
public sealed record WatchEventError(string ProjectId, string WatchPath, string Message);

/// <summary>
///     FileSystemWatcher adapter: one watcher per registered (projectId, path), all four event
///     types translated to WatchEvent with D3-normalized paths. Never throws on its own —
///     adapter-level failures surface as synthetic WatchEventError events (feature rule 13).
/// </summary>
public sealed partial class WatchEventSource(
    Action<WatchEvent> onEvent,
    Action<WatchEventError> onError,
    ILogger<WatchEventSource> logger) : IDisposable
{
    // File-mode registrations: (projectId, registeredPath) → the watched file. The
    // underlying watcher watches the PARENT directory (FileSystemWatcher requires a
    // directory); Translate filters events to this target file.
    private readonly Dictionary<(string ProjectId, string Path), string> _fileTargets =
        new(WatchKeyComparer.Instance);

    private readonly object _gate = new();

    private readonly Dictionary<(string ProjectId, string Path), FileSystemWatcher> _watchers =
        new(WatchKeyComparer.Instance);

    public void Dispose() => StopAll();

    /// <summary>Creates + enables the watcher for (projectId, path); idempotent; failures become error events.</summary>
    public void Start(string projectId, string path)
    {
        if (!TryNormalize(path, out var normalized))
        {
            ReportError(projectId, path, new ArgumentException($"Watch path '{path}' is not a valid path."));
            return;
        }

        lock (_gate)
        {
            if (_watchers.ContainsKey((projectId, normalized)))
            {
                return;
            }

            // A registered FILE path (or a missing path whose parent exists) cannot be
            // watched directly — FileSystemWatcher requires a directory. Watch the parent
            // filtered to the file name and translate only events for that file
            // (audit F7 / issue #44). A missing path with no existing parent still errors.
            var watchDirectory = normalized;
            string? filter = null;
            var includeSubdirectories = true;
            string? fileTarget = null;
            if (!Directory.Exists(normalized))
            {
                var parent = Path.GetDirectoryName(normalized);
                if (parent is null || !Directory.Exists(parent))
                {
                    ReportError(projectId, normalized,
                        new ArgumentException($"Watch path '{path}' is not a valid path."));
                    return;
                }

                watchDirectory = parent;
                filter = Path.GetFileName(normalized);
                includeSubdirectories = false;
                fileTarget = normalized;
            }

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(watchDirectory)
                {
                    IncludeSubdirectories = includeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                if (filter is not null)
                {
                    watcher.Filter = filter;
                }

                watcher.Created += (_, e) => HandleCreated(projectId, normalized, e);
                watcher.Changed += (_, e) => HandleChanged(projectId, normalized, e);
                watcher.Deleted += (_, e) => HandleDeleted(projectId, normalized, e);
                watcher.Renamed += (_, e) => HandleRenamed(projectId, normalized, e);
                watcher.Error += (_, e) => HandleError(projectId, normalized, e);
                watcher.EnableRaisingEvents = true;
                _watchers[(projectId, normalized)] = watcher;
                if (fileTarget is not null)
                {
                    _fileTargets[(projectId, normalized)] = fileTarget;
                }
            }
            catch (Exception ex)
            {
                watcher?.Dispose();
                ReportError(projectId, normalized, ex);
            }
        }
    }

    public void Stop(string projectId, string path)
    {
        if (!TryNormalize(path, out var normalized))
        {
            return;
        }

        lock (_gate)
        {
            _fileTargets.Remove((projectId, normalized));
            if (_watchers.Remove((projectId, normalized), out var watcher))
            {
                watcher.Dispose();
            }
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _fileTargets.Clear();
        }
    }

    public bool IsWatching(string projectId, string path)
    {
        if (!TryNormalize(path, out var normalized))
        {
            return false;
        }

        lock (_gate)
        {
            return _watchers.ContainsKey((projectId, normalized));
        }
    }

    internal void HandleCreated(string projectId, string watchPath, FileSystemEventArgs e) => Translate(projectId, watchPath, e.FullPath, WatchEventKind.Created, null);

    internal void HandleChanged(string projectId, string watchPath, FileSystemEventArgs e) => Translate(projectId, watchPath, e.FullPath, WatchEventKind.Changed, null);

    internal void HandleDeleted(string projectId, string watchPath, FileSystemEventArgs e) => Translate(projectId, watchPath, e.FullPath, WatchEventKind.Deleted, null);

    internal void HandleRenamed(string projectId, string watchPath, RenamedEventArgs e) => Translate(projectId, watchPath, e.FullPath, WatchEventKind.Renamed, e.OldFullPath);

    internal void HandleError(string projectId, string watchPath, ErrorEventArgs e) => ReportError(projectId, watchPath, e.GetException() ?? new IOException("FileSystemWatcher error"));

    private void Translate(string projectId, string watchPath, string fullPath, WatchEventKind kind, string? oldPath)
    {
        try
        {
            // File-mode watch: the underlying watcher sits on the parent directory, so
            // sibling events arrive too — translate only the registered file's events.
            string? fileTarget;
            lock (_gate)
            {
                fileTarget = _fileTargets.GetValueOrDefault((projectId, watchPath));
            }

            if (fileTarget is not null)
            {
                var normalizedFull = IngestPath.Normalize(fullPath);
                if (kind == WatchEventKind.Renamed && oldPath is not null)
                {
                    var normalizedOld = IngestPath.Normalize(oldPath);
                    if (IngestPath.PathComparer.Equals(normalizedOld, fileTarget) &&
                        !IngestPath.PathComparer.Equals(normalizedFull, fileTarget))
                    {
                        // Rename-away: the watched file left its registered name — the
                        // registration is now gone, so surface a Deleted for the target
                        // (a Renamed would make DigestAsync ingest the new name).
                        onEvent(new WatchEvent(projectId, fileTarget, WatchEventKind.Deleted));
                    }
                    else if (IngestPath.PathComparer.Equals(normalizedFull, fileTarget))
                    {
                        onEvent(new WatchEvent(projectId, fileTarget, WatchEventKind.Renamed, normalizedOld));
                    }

                    return;
                }

                if (!IngestPath.PathComparer.Equals(normalizedFull, fileTarget))
                {
                    return; // sibling event under the watched parent — not ours
                }
            }

            onEvent(new WatchEvent(projectId, IngestPath.Normalize(fullPath), kind,
                oldPath is null ? null : IngestPath.Normalize(oldPath)));
        }
        catch (Exception ex)
        {
            ReportError(projectId, watchPath, ex);
        }
    }

    private void ReportError(string projectId, string watchPath, Exception exception)
    {
        try
        {
            onError(new WatchEventError(projectId, watchPath, exception.Message));
        }
        catch (Exception ex)
        {
            Log.ErrorCallbackFailed(logger, ex);
        }

        Log.WatchError(logger, projectId, watchPath, exception);
    }

    private static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = IngestPath.Normalize(path);
            return true;
        }
        catch (Exception)
        {
            normalized = path;
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 300, Level = LogLevel.Error,
            Message = "Watch event source error for {ProjectId} on {WatchPath}")]
        public static partial void WatchError(ILogger logger, string projectId, string watchPath,
            Exception exception);

        [LoggerMessage(EventId = 301, Level = LogLevel.Error, Message = "Watch event source error callback failed")]
        public static partial void ErrorCallbackFailed(ILogger logger, Exception exception);
    }
}
