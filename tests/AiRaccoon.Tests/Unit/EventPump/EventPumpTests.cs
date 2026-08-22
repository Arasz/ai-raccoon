using AiRaccoon.Core.EventPump;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.EventPump;

/// <summary>
///     The bounded-channel pump's own contract, extracted from the metrics buffer (owner ruling
///     G17, docs/work/2026-08-22-post-delta-3-plan.md WP11-B1): capacity enforcement, FIFO
///     draining, coalescing, and the wake-up a plain queue cannot give. Counts and ordering only —
///     no wall-clock assertions (owner ruling #464).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EventPumpTests
{
    private sealed record Item(int Value);

    [Fact]
    public void TryEnqueue_BeyondCapacity_ReturnsFalseAndCountsTheDrop()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 2, Coalesce: false));

        pump.TryEnqueue(new Item(1)).ShouldBeTrue();
        pump.TryEnqueue(new Item(2)).ShouldBeTrue();
        pump.TryEnqueue(new Item(3)).ShouldBeFalse();

        pump.EnqueuedCount.ShouldBe(2);
        pump.DroppedCount.ShouldBe(1);
    }

    [Fact]
    public async Task TryEnqueue_FullPump_ReturnsFalseInsteadOfWaiting()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 1, Capacity: 1, Coalesce: false));
        pump.TryEnqueue(new Item(1)).ShouldBeTrue();

        // A blocking WriteAsync under FullMode.Wait would hang here forever (nothing drains);
        // a bounded wait proves TryEnqueue returns on the calling thread instead.
        var enqueue = Task.Run(() => pump.TryEnqueue(new Item(2)), TestContext.Current.CancellationToken);
        var hangGuard = Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(enqueue, hangGuard);

        finished.ShouldBe(enqueue, "TryEnqueue must never block waiting for space");
        (await enqueue).ShouldBeFalse();
    }

    [Fact]
    public void DrainUpTo_BudgetSmallerThanBacklog_TakesExactlyTheBudgetInOrder()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: false));
        for (var i = 1; i <= 5; i++)
        {
            pump.TryEnqueue(new Item(i));
        }

        var first = pump.DrainUpTo(2);
        var second = pump.DrainUpTo(2);

        first.Select(i => i.Value).ShouldBe([1, 2]);
        second.Select(i => i.Value).ShouldBe([3, 4]);
    }

    [Fact]
    public void DrainUpTo_EmptyPump_ReturnsEmpty()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: false));

        var drained = pump.DrainUpTo(5);

        drained.Count.ShouldBe(0);
    }

    [Fact]
    public void TryEnqueue_CoalescingTopic_IdenticalItemIsNotQueuedTwice()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: true));
        var item = new Item(1);

        pump.TryEnqueue(item).ShouldBeTrue();
        pump.TryEnqueue(item).ShouldBeFalse();
        pump.TryEnqueue(item).ShouldBeFalse();

        pump.CoalescedCount.ShouldBe(2);
        pump.DrainUpTo(10).Count.ShouldBe(1);
    }

    [Fact]
    public void DrainUpTo_ReleasesTheCoalesceKey_SoAnItemArrivingAfterTheTakeQueuesAgain()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: true));
        var item = new Item(1);
        pump.TryEnqueue(item);

        pump.DrainUpTo(10);
        var requeued = pump.TryEnqueue(item);

        requeued.ShouldBeTrue("the coalesce key is released at the take, not at completion — no lost wake-up");
        pump.DrainUpTo(10).Count.ShouldBe(1);
    }

    [Fact]
    public void TryEnqueue_NonCoalescingTopic_KeepsDuplicates()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: false));
        var item = new Item(1);

        pump.TryEnqueue(item).ShouldBeTrue();
        pump.TryEnqueue(item).ShouldBeTrue();
        pump.TryEnqueue(item).ShouldBeTrue();

        pump.CoalescedCount.ShouldBe(0);
        pump.DrainUpTo(10).Count.ShouldBe(3);
    }

    [Fact]
    public void ApplyCapacity_ChangesTheCapForFutureEnqueues_NotWhatIsQueued()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 1, Coalesce: false));
        pump.TryEnqueue(new Item(1)).ShouldBeTrue();
        pump.TryEnqueue(new Item(2)).ShouldBeFalse();

        pump.DrainUpTo(10);
        pump.ApplyCapacity(2);

        pump.TryEnqueue(new Item(3)).ShouldBeTrue();
        pump.TryEnqueue(new Item(4)).ShouldBeTrue();
        pump.TryEnqueue(new Item(5)).ShouldBeFalse();
    }

    [Fact]
    public async Task WaitForItemAsync_CompletesOnceAnItemArrives()
    {
        var pump = new EventPump<Item>(new PumpTopic(Ceiling: 10, Capacity: 10, Coalesce: false));
        pump.TryEnqueue(new Item(1));

        var wait = pump.WaitForItemAsync(TestContext.Current.CancellationToken);
        await wait;

        wait.IsCompletedSuccessfully.ShouldBeTrue();
    }
}
