using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

public interface IWatchRetryPolicy
{
    /// <summary>True when the watch may attempt a digest now: not stopped and past any backoff.</summary>
    bool ShouldAttempt(string projectId, string watchPath, DateTimeOffset now);

    bool IsStopped(string projectId, string watchPath);

    /// <summary>Records a failed digest; returns the watch state after this failure.</summary>
    WatchState RecordFailure(string projectId, string watchPath, DateTimeOffset now);

    /// <summary>Resets the counter — checking continues with a clean slate.</summary>
    void RecordSuccess(string projectId, string watchPath);

    void Forget(string projectId, string watchPath);
}
