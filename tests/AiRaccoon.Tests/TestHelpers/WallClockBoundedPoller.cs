using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Bounded poll: advance the fake clock, run one tick, sleep briefly for OS event delivery —
///     until the condition holds or either budget expires. Every await (condition, tick,
///     inter-step delay) races the remaining real-time budget, so a blocked await fails fast
///     with the blocked step named instead of hanging the caller indefinitely.
///     Extracted from <c>WatchIntegrationTests.Stack.StepUntilAsync</c> (plan Q1 / M7-QA F2).
/// </summary>
internal sealed class WallClockBoundedPoller(FakeTimeProvider time)
{
    public async Task<bool> StepUntilAsync(
        Func<Task<bool>> condition,
        Func<CancellationToken, Task> tick,
        CancellationToken cancellationToken,
        int maxFakeSeconds = 60,
        int maxRealSeconds = 30,
        Action<string>? onGiveUp = null)
    {
        var startedAt = DateTime.UtcNow;
        var realDeadline = startedAt.AddSeconds(maxRealSeconds);
        var fakeStart = time.GetUtcNow();
        var steps = 0;

        while (!await RaceAgainstBudgetAsync(condition, "condition", startedAt, realDeadline, steps, cancellationToken)
                   .ConfigureAwait(false))
        {
            var fakeSpent = time.GetUtcNow() - fakeStart;
            var fakeExpired = fakeSpent >= TimeSpan.FromSeconds(maxFakeSeconds);
            var realExpired = DateTime.UtcNow >= realDeadline;
            if (fakeExpired || realExpired)
            {
                onGiveUp?.Invoke(
                    $"StepUntilAsync gave up after {steps} steps: " +
                    $"{(fakeExpired ? "fake-time budget" : "real-time hang-stop")} expired " +
                    $"(fake {fakeSpent.TotalSeconds:F1}s/{maxFakeSeconds}s, " +
                    $"real {(DateTime.UtcNow - startedAt).TotalSeconds:F1}s/{maxRealSeconds}s)");
                return false;
            }

            steps++;
            time.Advance(TimeSpan.FromMilliseconds(100));

            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = realDeadline - DateTime.UtcNow;
            budgetCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

            await RaceAgainstBudgetAsync(() => tick(budgetCts.Token), "TickOnceAsync", startedAt, realDeadline, steps,
                cancellationToken).ConfigureAwait(false);
            await RaceAgainstBudgetAsync(() => Task.Delay(20, budgetCts.Token), "inter-step delay", startedAt,
                realDeadline, steps, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    ///     Starts <paramref name="starter" /> and races it against the remaining real-time budget.
    ///     The started task is not itself cancelled if it ignores its own token (e.g. the
    ///     token-less <c>condition</c> delegate) — the race only bounds how long the caller waits.
    /// </summary>
    private static async Task RaceAgainstBudgetAsync(Func<Task> starter, string stepName, DateTime startedAt,
        DateTime realDeadline, int steps, CancellationToken cancellationToken)
    {
        await RaceAgainstBudgetAsync<object?>(async () =>
        {
            await starter().ConfigureAwait(false);
            return null;
        }, stepName, startedAt, realDeadline, steps, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> RaceAgainstBudgetAsync<T>(Func<Task<T>> starter, string stepName, DateTime startedAt,
        DateTime realDeadline, int steps, CancellationToken cancellationToken)
    {
        var remaining = realDeadline - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var task = starter();
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(remaining, delayCts.Token);
        var winner = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
        if (winner != delayTask)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"StepUntilAsync: '{stepName}' was still blocked after {steps} steps and " +
            $"{(DateTime.UtcNow - startedAt).TotalSeconds:F1}s of wall clock — exceeded its real-time budget.");
    }
}
