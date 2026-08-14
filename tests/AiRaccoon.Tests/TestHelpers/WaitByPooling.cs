namespace AiRaccoon.Tests.TestHelpers;

public static class WaitByPooling
{
    private const int IterationLimit = 5;
    private static readonly TimeSpan FirstTick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitDeadline = TimeSpan.FromSeconds(120);

    public static ValueTask<bool> WaitForAsync(Func<ValueTask<bool>> predicate, TimeProvider timeProvider, CancellationToken
        cancellationToken) =>
        WaitForAsync(predicate, static asStatePredicate => asStatePredicate(), IterationLimit, FirstTick, WaitDeadline, timeProvider, cancellationToken);

    public static ValueTask<bool> WaitForAsync<T>(T state, Func<T, ValueTask<bool>> predicate, TimeProvider timeProvider, CancellationToken
        cancellationToken) =>
        WaitForAsync(state, predicate, IterationLimit, FirstTick, WaitDeadline, timeProvider, cancellationToken);

    public static async ValueTask<bool> WaitForAsync<T>(T state, Func<T, ValueTask<bool>> predicate, int iterationLimit, TimeSpan firstTick, TimeSpan waitDeadline, TimeProvider timeProvider,
        CancellationToken
            cancellationToken)
    {
        var iteration = 0;
        using var deadlineCts = new CancellationTokenSource(waitDeadline, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
        using var timer = new PeriodicTimer(firstTick, timeProvider);
        while (await timer.WaitForNextTickAsync(linkedCts.Token))
        {
            iteration++;
            if (iteration >= iterationLimit)
            {
                return false;
            }

            timer.Period = firstTick * (iteration + 1) + Drift();

            if (await predicate(state))
            {
                return true;
            }
        }

        return false;

        static TimeSpan Drift() => TimeSpan.FromSeconds(Random.Shared.Next(0, 10));
    }
}
