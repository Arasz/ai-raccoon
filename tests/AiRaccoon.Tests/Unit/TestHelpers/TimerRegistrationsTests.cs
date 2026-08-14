using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins the signal that replaces "sleep and hope the timer registered": the count only rises on a
///     real CreateTimer, and the wait resolves false rather than hanging when it never happens.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TimerRegistrationsTests
{
    [Fact]
    public async Task ATimerCreatedOnTheInnerProvider_IsCounted()
    {
        var timers = new TimerRegistrations(new FakeTimeProvider());
        timers.Count.ShouldBe(0);

        using var timer = timers.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        timers.Count.ShouldBe(1);
        (await timers.WaitForAsync(1, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitingForMoreTimersThanAreEverCreated_ReturnsFalse()
    {
        // The caller reports "the code under test never registered its timer" instead of hanging.
        var timers = new TimerRegistrations(new FakeTimeProvider());
        using var timer = timers.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        var settled = await WaitByPolling.WaitForAsync(
            () => ValueTask.FromResult(false),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            TimeProvider.System, TestContext.Current.CancellationToken);

        settled.ShouldBeFalse();
        timers.Count.ShouldBe(1);
    }

    [Fact]
    public void TheClockIsStillTheInnerOne()
    {
        // A decorator that shadowed the fake clock would make every Advance-based test lie.
        var clock = new FakeTimeProvider();
        var timers = new TimerRegistrations(clock);

        clock.Advance(TimeSpan.FromMinutes(5));

        timers.GetUtcNow().ShouldBe(clock.GetUtcNow());
    }
}
