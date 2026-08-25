using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Pins down <see cref="FakeClockPoller" />: the verdict comes from the fake-time step budget
///     or the condition, never from the wall clock; a blocked await ends with the caller's token.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StepUntilAsyncTests
{
    [RetryFact]
    public async Task ABlockedCondition_EndsOnlyWithTheCallersCancellation()
    {
        var poller = new FakeClockPoller(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var neverCompletes = new TaskCompletionSource<bool>();
        var entered = new TaskCompletionSource();
        using var caller = new CancellationTokenSource();
        var gaveUp = false;

        var poll = poller.StepUntilAsync(
            condition: () =>
            {
                entered.TrySetResult();
                return neverCompletes.Task;
            },
            tick: _ => Task.CompletedTask,
            cancellationToken: caller.Token,
            onGiveUp: _ => gaveUp = true);

        await entered.Task;
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => poll);
        gaveUp.ShouldBeFalse("a blocked await is a hang, not a give-up — the two must stay distinguishable");
    }

    [RetryFact]
    public async Task ABlockedTick_EndsOnlyWithTheCallersCancellation()
    {
        var poller = new FakeClockPoller(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var neverCompletes = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var caller = new CancellationTokenSource();

        var poll = poller.StepUntilAsync(
            condition: () => Task.FromResult(false),
            tick: _ =>
            {
                entered.TrySetResult();
                return neverCompletes.Task;
            },
            cancellationToken: caller.Token);

        await entered.Task;
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => poll);
    }

    [RetryFact]
    public async Task AConditionThatBecomesTrue_ReturnsTrueAndStopsCallingTick()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var poller = new FakeClockPoller(time);
        var conditionCalls = 0;
        var tickCalls = 0;

        var result = await poller.StepUntilAsync(
            condition: () =>
            {
                conditionCalls++;
                return Task.FromResult(conditionCalls > 3);
            },
            tick: _ =>
            {
                tickCalls++;
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
        tickCalls.ShouldBe(3, "one tick per failed condition check before it finally succeeds, and no more");
    }

    [RetryFact]
    public async Task AConditionThatNeverHoldsButNeverBlocks_GivesUpOnTheFakeBudgetAndReturnsFalse()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var poller = new FakeClockPoller(time);
        string? giveUpMessage = null;

        var result = await poller.StepUntilAsync(
            condition: () => Task.FromResult(false),
            tick: _ => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken,
            maxFakeSeconds: 1,
            onGiveUp: message => giveUpMessage = message);

        result.ShouldBeFalse();
        giveUpMessage.ShouldNotBeNull();
        giveUpMessage.ShouldContain("fake-time budget");
        (time.GetUtcNow() - time.Start).ShouldBe(TimeSpan.FromSeconds(1),
            "ten 100ms steps spend exactly the one-second fake budget — the verdict is a step count, not a clock");
    }
}
