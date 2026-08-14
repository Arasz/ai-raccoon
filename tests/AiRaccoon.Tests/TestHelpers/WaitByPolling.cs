using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Polls a condition with capped exponential backoff. Returns false when the deadline passes so
///     the caller can throw a diagnostic naming what it waited for; only caller cancellation throws.
/// </summary>
public static class WaitByPolling
{
    /// <summary>First poll lands a tick after the condition is first checked, so a fast boot is seen fast.</summary>
    public static readonly TimeSpan DefaultFirstTick = TimeSpan.FromMilliseconds(25);

    /// <summary>Steady-state poll floor: ~2/s, cheap enough for a loaded 2-core CI runner.</summary>
    public static readonly TimeSpan DefaultMaxTick = TimeSpan.FromMilliseconds(500);

    /// <summary>Generous enough for a cold process spawn on a contended runner.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(120);

    private const double Growth = 1.6;
    private const double JitterFraction = 0.2;

    public static ValueTask<bool> WaitForAsync(
        Func<ValueTask<bool>> predicate, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        WaitForAsync(predicate, static asStatePredicate => asStatePredicate(), DefaultFirstTick, DefaultMaxTick,
            DefaultDeadline, timeProvider, cancellationToken);

    public static ValueTask<bool> WaitForAsync(
        Func<ValueTask<bool>> predicate, TimeSpan firstTick, TimeSpan maxTick, TimeSpan deadline,
        TimeProvider timeProvider, CancellationToken cancellationToken) =>
        WaitForAsync(predicate, static asStatePredicate => asStatePredicate(), firstTick, maxTick, deadline,
            timeProvider, cancellationToken);

    public static ValueTask<bool> WaitForAsync<T>(
        T state, Func<T, ValueTask<bool>> predicate, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        WaitForAsync(state, predicate, DefaultFirstTick, DefaultMaxTick, DefaultDeadline, timeProvider,
            cancellationToken);

    public static async ValueTask<bool> WaitForAsync<T>(
        T state, Func<T, ValueTask<bool>> predicate, TimeSpan firstTick, TimeSpan maxTick, TimeSpan deadline,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(predicate);
        Guard.IsNotNull(timeProvider);
        Guard.IsGreaterThan(firstTick, TimeSpan.Zero);
        Guard.IsGreaterThanOrEqualTo(maxTick, firstTick);

        using var deadlineCts = new CancellationTokenSource(deadline, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        try
        {
            // Check before the first tick: a condition that already holds must cost no wall clock.
            if (await predicate(state))
            {
                return true;
            }

            var period = firstTick;
            using var timer = new PeriodicTimer(period, timeProvider);
            while (await timer.WaitForNextTickAsync(linkedCts.Token))
            {
                if (await predicate(state))
                {
                    return true;
                }

                period = NextPeriod(period, maxTick);
                timer.Period = period;
            }

            return false;
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // The deadline is an answer the caller reports, not a failure it propagates.
            return false;
        }
    }

    private static TimeSpan NextPeriod(TimeSpan current, TimeSpan max)
    {
        var grown = current * Growth;
        var jittered = grown + grown * (Random.Shared.NextDouble() * JitterFraction);
        return jittered > max ? max : jittered;
    }
}
