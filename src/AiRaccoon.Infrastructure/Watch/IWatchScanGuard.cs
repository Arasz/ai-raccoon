namespace AiRaccoon.Infrastructure.Watch;

public interface IWatchScanGuard : IDisposable
{
    int StartedScans { get; }
    int SkippedScans { get; }

    /// <summary>
    ///     Starts <paramref name="scan" /> for the watch unless one is already running, in which
    ///     case the caller joins the running scan's task. The scan always runs off-thread (async).
    /// </summary>
    Task Run(string projectId, string path, Func<CancellationToken, Task> scan,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the in-flight scan for one watch, if any; other watches are unaffected.</summary>
    void Cancel(string projectId, string path);

    /// <summary>Cancels every in-flight scan (host shutdown).</summary>
    void CancelAll();

    bool IsRunning(string projectId, string path);
}
