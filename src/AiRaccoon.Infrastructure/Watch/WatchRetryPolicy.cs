using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Per-watch consecutive-failure counter with exponential backoff: after 5 consecutive
///     failures a watch stops being checked (registration + status are kept, feature rule 14).
/// </summary>
public sealed class WatchRetryPolicy
{
    private const int MaxFailures = 5;

    private readonly Dictionary<(string ProjectId, string Path), Entry> _entries = new(WatchKeyComparer.Instance);

    /// <summary>Backoff after failure #n: 1s, 2s, 4s, 8s (exponential).</summary>
    public static TimeSpan BackoffFor(int consecutiveFailures) => TimeSpan.FromSeconds(1L << consecutiveFailures - 1);

    /// <summary>True when the watch may attempt a digest now: not stopped and past any backoff.</summary>
    public bool ShouldAttempt(string projectId, string watchPath, DateTimeOffset now)
    {
        if (!_entries.TryGetValue((projectId, watchPath), out var entry) || entry.ConsecutiveFailures == 0)
        {
            return true;
        }

        return entry.ConsecutiveFailures < MaxFailures && entry.NextAttemptAt is { } next && now >= next;
    }

    public bool IsStopped(string projectId, string watchPath) => _entries.TryGetValue((projectId, watchPath), out var entry) && entry.ConsecutiveFailures >= MaxFailures;

    /// <summary>Records a failed digest; returns the watch state after this failure.</summary>
    public WatchState RecordFailure(string projectId, string watchPath, DateTimeOffset now)
    {
        var failures = _entries.TryGetValue((projectId, watchPath), out var entry)
            ? entry.ConsecutiveFailures + 1
            : 1;
        DateTimeOffset? nextAttempt = failures >= MaxFailures
            ? null
            : now + BackoffFor(failures);
        _entries[(projectId, watchPath)] = new Entry(failures, nextAttempt);
        return failures >= MaxFailures ? WatchState.Stopped : WatchState.Retrying;
    }

    /// <summary>Resets the counter — checking continues with a clean slate.</summary>
    public void RecordSuccess(string projectId, string watchPath) => _entries.Remove((projectId, watchPath));

    public void Forget(string projectId, string watchPath) => _entries.Remove((projectId, watchPath));

    private sealed record Entry(int ConsecutiveFailures, DateTimeOffset? NextAttemptAt);
}
