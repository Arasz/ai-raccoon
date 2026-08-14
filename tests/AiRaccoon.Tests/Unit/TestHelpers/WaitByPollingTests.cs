using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins the polling helper's contract: an already-true predicate costs no wall clock, an
///     expired deadline returns false rather than throwing, and caller cancellation still throws.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WaitByPollingTests
{
    private static readonly TimeSpan FirstTick = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaxTick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task AnAlreadyTruePredicate_ResolvesWithoutAdvancingTheClock()
    {
        // The floor this pins: a server that is already listening must not pay a polling tick.
        var time = new FakeTimeProvider();
        var evaluations = 0;

        var wait = WaitByPolling.WaitForAsync(() =>
        {
            evaluations++;
            return ValueTask.FromResult(true);
        }, FirstTick, MaxTick, Deadline, time, TestContext.Current.CancellationToken);

        (await wait).ShouldBeTrue();
        evaluations.ShouldBe(1);
        time.GetUtcNow().ShouldBe(new FakeTimeProvider().GetUtcNow(), TimeSpan.Zero,
            "the predicate must be evaluated before the first tick, not after it");
    }

    [Fact]
    public async Task APredicateThatNeverPasses_ReturnsFalseAtTheDeadline()
    {
        // Returning false is what lets a caller throw a diagnostic naming what it was waiting for.
        var time = new FakeTimeProvider();
        var wait = WaitByPolling.WaitForAsync(() => ValueTask.FromResult(false),
            FirstTick, MaxTick, Deadline, time, TestContext.Current.CancellationToken).AsTask();

        await PumpAsync(wait, time, Deadline + TimeSpan.FromSeconds(1));

        wait.IsCompletedSuccessfully.ShouldBeTrue("the deadline is an answer, not an exception");
        (await wait).ShouldBeFalse();
    }

    [Fact]
    public async Task APredicateThatPassesLater_IsPolledManyTimesWithinTheFirstSeconds()
    {
        // Coarse polling hides a boot that succeeds between two widely spaced ticks.
        var time = new FakeTimeProvider();
        var evaluations = 0;

        var wait = WaitByPolling.WaitForAsync(() =>
        {
            evaluations++;
            return ValueTask.FromResult(evaluations >= 20);
        }, FirstTick, MaxTick, Deadline, time, TestContext.Current.CancellationToken).AsTask();

        await PumpAsync(wait, time, TimeSpan.FromSeconds(10));

        (await wait).ShouldBeTrue();
        evaluations.ShouldBe(20);
    }

    [Fact]
    public async Task CallerCancellation_StillThrows()
    {
        // A cancelled test run is a cancellation, not a "condition never happened" answer.
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var wait = WaitByPolling.WaitForAsync(() => ValueTask.FromResult(false),
            FirstTick, MaxTick, Deadline, time, cts.Token).AsTask();

        await cts.CancelAsync();
        time.Advance(FirstTick);

        await Should.ThrowAsync<OperationCanceledException>(() => wait);
    }

    /// <summary>Advances the fake clock until the wait settles or the fake budget is spent.</summary>
    private static async Task PumpAsync(Task wait, FakeTimeProvider time, TimeSpan fakeBudget)
    {
        var spent = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(5);
        while (!wait.IsCompleted && spent < fakeBudget)
        {
            time.Advance(step);
            spent += step;
            await Task.Yield();
        }
    }
}
