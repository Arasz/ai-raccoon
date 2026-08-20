using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Counts the timers created against an inner <see cref="TimeProvider" /> so a test can wait for
///     them to exist instead of sleeping and hoping. Advancing a fake clock before the code under
///     test has registered its timer silently does nothing.
/// </summary>
public sealed class TimerRegistrations(TimeProvider inner) : TimeProvider
{
    private int _created;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public int Count => Volatile.Read(ref _created);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _created);
        return timer;
    }

    /// <summary>Resolves once at least <paramref name="count" /> timers exist; false if they never do.</summary>
    public ValueTask<bool> WaitForAsync(int count, CancellationToken cancellationToken)
    {
        Guard.IsGreaterThan(count, 0);

        return WaitByPolling.WaitForAsync(
            () => ValueTask.FromResult(Count >= count), System, cancellationToken);
    }
}
