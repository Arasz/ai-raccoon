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
public sealed class WatchEventSource(
    Action<WatchEvent> onEvent,
    Action<WatchEventError> onError,
    ILogger<WatchEventSource> logger) : IDisposable
{
    public void Start(string projectId, string path)
    {
        _ = (onEvent, onError, logger, projectId, path);
        throw new NotImplementedException();
    }

    public void Stop(string projectId, string path)
    {
        _ = (onEvent, onError, logger, projectId, path);
        throw new NotImplementedException();
    }

    public void StopAll()
    {
        _ = (onEvent, onError, logger);
        throw new NotImplementedException();
    }

    public bool IsWatching(string projectId, string path)
    {
        _ = (onEvent, onError, logger, projectId, path);
        throw new NotImplementedException();
    }

    public void Dispose() => StopAll();

    internal void HandleCreated(string projectId, string watchPath, FileSystemEventArgs e)
    {
        _ = (onEvent, onError, logger, projectId, watchPath, e);
        throw new NotImplementedException();
    }

    internal void HandleChanged(string projectId, string watchPath, FileSystemEventArgs e)
    {
        _ = (onEvent, onError, logger, projectId, watchPath, e);
        throw new NotImplementedException();
    }

    internal void HandleDeleted(string projectId, string watchPath, FileSystemEventArgs e)
    {
        _ = (onEvent, onError, logger, projectId, watchPath, e);
        throw new NotImplementedException();
    }

    internal void HandleRenamed(string projectId, string watchPath, RenamedEventArgs e)
    {
        _ = (onEvent, onError, logger, projectId, watchPath, e);
        throw new NotImplementedException();
    }

    internal void HandleError(string projectId, string watchPath, ErrorEventArgs e)
    {
        _ = (onEvent, onError, logger, projectId, watchPath, e);
        throw new NotImplementedException();
    }
}
