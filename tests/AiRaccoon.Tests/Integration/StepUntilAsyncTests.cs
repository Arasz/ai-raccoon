using System.Diagnostics;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Pins down <see cref="WallClockBoundedPoller" />, the piece extracted from
///     <c>WatchIntegrationTests.Stack.StepUntilAsync</c> so a blocked await inside the poll loop
///     fails within its wall-clock budget instead of hanging the testhost (plan Q1 / M7-QA F2).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StepUntilAsyncTests
{
    [Fact]
    public async Task ABlockedAwait_FailsWithinTheWallClockBudget()
    {
        var poller = new WallClockBoundedPoller(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var neverCompletes = new TaskCompletionSource<bool>();
        var gaveUp = false;
        var stopwatch = Stopwatch.StartNew();

        var exception = await Should.ThrowAsync<TimeoutException>(() =>
            poller.StepUntilAsync(
                condition: () => neverCompletes.Task,
                tick: _ => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken,
                maxRealSeconds: 2,
                onGiveUp: _ => gaveUp = true));
        stopwatch.Stop();

        exception.Message.ShouldContain("condition");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        gaveUp.ShouldBeFalse("a blocked await is a hang, not a normal give-up — the two must stay distinguishable");
    }

    [Fact]
    public async Task ABlockedTick_FailsWithinTheWallClockBudgetNamingTickOnceAsync()
    {
        var poller = new WallClockBoundedPoller(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var neverCompletes = new TaskCompletionSource();
        var stopwatch = Stopwatch.StartNew();

        var exception = await Should.ThrowAsync<TimeoutException>(() =>
            poller.StepUntilAsync(
                condition: () => Task.FromResult(false),
                tick: _ => neverCompletes.Task,
                cancellationToken: TestContext.Current.CancellationToken,
                maxRealSeconds: 2));
        stopwatch.Stop();

        exception.Message.ShouldContain("TickOnceAsync");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACallerCancellation_FailsPromptlyWithOperationCanceledInsteadOfTimeout()
    {
        var poller = new WallClockBoundedPoller(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var neverCompletes = new TaskCompletionSource<bool>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            poller.StepUntilAsync(
                condition: () => neverCompletes.Task,
                tick: _ => Task.CompletedTask,
                cancellationToken: cts.Token,
                maxRealSeconds: 30));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            "caller cancellation must be prompt, not deferred until the (much larger) wall-clock budget expires");
    }

    [Fact]
    public async Task AConditionThatBecomesTrue_ReturnsTrueAndStopsCallingTick()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var poller = new WallClockBoundedPoller(time);
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

    [Fact]
    public async Task AConditionThatNeverHoldsButNeverBlocks_GivesUpAndReturnsFalseWithoutThrowing()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var poller = new WallClockBoundedPoller(time);
        string? giveUpMessage = null;

        var result = await poller.StepUntilAsync(
            condition: () => Task.FromResult(false),
            tick: _ => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken,
            maxFakeSeconds: 1,
            maxRealSeconds: 30,
            onGiveUp: message => giveUpMessage = message);

        result.ShouldBeFalse();
        giveUpMessage.ShouldNotBeNull();
        giveUpMessage.ShouldContain("fake-time budget");
    }
}
