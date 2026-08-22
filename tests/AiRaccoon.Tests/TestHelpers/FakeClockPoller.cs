using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Bounded poll: advance the fake clock, run one tick, yield briefly for OS event delivery —
///     until the condition holds or the fake-time (step) budget expires. No wall clock decides the
///     outcome (owner ruling, PR #464): a blocked await ends only with the caller's cancellation
///     token, which the test run supplies. Extracted from
///     <c>WatchIntegrationTests.Stack.StepUntilAsync</c> (plan Q1 / M7-QA F2).
/// </summary>
internal sealed class FakeClockPoller(FakeTimeProvider time)
{
    /// <summary>Real pause between steps so the OS can deliver file events; pacing, never a verdict.</summary>
    public static TimeSpan EventDeliveryPause { get; } = TimeSpan.FromMilliseconds(20);

    public async Task<bool> StepUntilAsync(
        Func<Task<bool>> condition,
        Func<CancellationToken, Task> tick,
        CancellationToken cancellationToken,
        int maxFakeSeconds = 60,
        Action<string>? onGiveUp = null)
    {
        var fakeStart = time.GetUtcNow();
        var steps = 0;
        while (!await condition().WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            var fakeSpent = time.GetUtcNow() - fakeStart;
            if (fakeSpent >= TimeSpan.FromSeconds(maxFakeSeconds))
            {
                onGiveUp?.Invoke(
                    $"StepUntilAsync gave up after {steps} steps: fake-time budget expired " +
                    $"(fake {fakeSpent.TotalSeconds:F1}s/{maxFakeSeconds}s)");
                return false;
            }

            steps++;
            time.Advance(TimeSpan.FromMilliseconds(100));
            await tick(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(EventDeliveryPause, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
