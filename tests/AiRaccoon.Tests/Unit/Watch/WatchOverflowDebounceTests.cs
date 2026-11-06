using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Overflow bursts: a burst is due after a quiet window, and never later than the max wait.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchOverflowDebounceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly WatchKey Key = new("acme", "/repo");

    private static WatchOverflowDebounce NewDebounce() => new(Quiet, MaxWait);

    [Fact]
    public void Due_BeforeTheQuietWindowEnds_IsEmpty()
    {
        var debounce = NewDebounce();
        debounce.Record(Key, Start);

        debounce.Due(Start + Quiet - TimeSpan.FromMilliseconds(1)).ShouldBeEmpty();
    }

    [Fact]
    public void Due_WhenTheQuietWindowEnds_ReturnsTheWatch()
    {
        var debounce = NewDebounce();
        debounce.Record(Key, Start);

        debounce.Due(Start + Quiet).ShouldBe([Key]);
    }

    [Fact]
    public void Record_DuringTheQuietWindow_RestartsIt()
    {
        var debounce = NewDebounce();
        debounce.Record(Key, Start);
        debounce.Record(Key, Start + TimeSpan.FromSeconds(1));

        debounce.Due(Start + Quiet).ShouldBeEmpty();
        debounce.Due(Start + TimeSpan.FromSeconds(1) + Quiet).ShouldBe([Key]);
    }

    [Fact]
    public void Due_UnderContinuousOverflow_FiresAtTheMaxWait()
    {
        var debounce = NewDebounce();
        var now = Start;
        while (now < Start + MaxWait)
        {
            debounce.Record(Key, now);
            debounce.Due(now).ShouldBeEmpty();
            now += TimeSpan.FromSeconds(1);
        }

        debounce.Record(Key, now);

        debounce.Due(now).ShouldBe([Key]);
    }

    [Fact]
    public void Take_ReturnsTheBurstSize_AndClearsIt()
    {
        var debounce = NewDebounce();
        debounce.Record(Key, Start);
        debounce.Record(Key, Start);
        debounce.Record(Key, Start);

        debounce.Take(Key).ShouldBe(3);

        debounce.Due(Start + MaxWait).ShouldBeEmpty();
        debounce.Take(Key).ShouldBe(0);
    }

    [Fact]
    public void Due_KeepsAnUntakenBurst()
    {
        var debounce = NewDebounce();
        debounce.Record(Key, Start);

        debounce.Due(Start + Quiet).ShouldBe([Key]);

        debounce.Due(Start + Quiet).ShouldBe([Key]);
    }

    [Fact]
    public void Bursts_OnDifferentWatches_AreIndependent()
    {
        var other = new WatchKey("acme", "/other");
        var debounce = NewDebounce();
        debounce.Record(Key, Start);
        debounce.Record(other, Start + TimeSpan.FromSeconds(1));

        debounce.Due(Start + Quiet).ShouldBe([Key]);
    }
}
