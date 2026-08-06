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

            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(normalized)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                watcher.Created += (_, e) => HandleCreated(projectId, normalized, e);
                watcher.Changed += (_, e) => HandleChanged(projectId, normalized, e);
                watcher.Deleted += (_, e) => HandleDeleted(projectId, normalized, e);
                watcher.Renamed += (_, e) => HandleRenamed(projectId, normalized, e);
                watcher.Error += (_, e) => HandleError(projectId, normalized, e);
                watcher.EnableRaisingEvents = true;
                _watchers[(projectId, normalized)] = watcher;
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
            onEvent(new WatchEvent(projectId, WatchPath.Normalize(fullPath), kind,
                oldPath is null ? null : WatchPath.Normalize(oldPath)));
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
            normalized = WatchPath.Normalize(path);
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
