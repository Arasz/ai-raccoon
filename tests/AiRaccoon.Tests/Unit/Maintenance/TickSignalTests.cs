using AiRaccoon.Infrastructure.Maintenance;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     D21 gap 4: <see cref="TickSignal" /> had no test file. Its contract: <c>WaitAsync</c> returns
///     true the instant <c>Count</c> reaches the target (already-met or woken by a later
///     <c>Increment</c>), false on timeout/cancellation, and <c>Increment</c> under concurrent callers
///     never loses an update or drops a waiter — <c>_gate</c> makes the check-then-register in
///     <c>WaitAsync</c> and the increment-then-notify in <c>Increment</c> mutually exclusive, so there
///     is no window where a waiter registers after the crossing increment already happened.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TickSignalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WaitAsync_TargetAlreadyReached_ReturnsTrueImmediately()
    {
        var signal = new TickSignal();
        signal.Increment();
        signal.Increment();

        var reached = await signal.WaitAsync(2, Patience, TestContext.Current.CancellationToken);

        reached.ShouldBeTrue();
        signal.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WaitAsync_TargetReachedByALaterIncrement_Wakes()
    {
        var signal = new TickSignal();
        var waiter = signal.WaitAsync(1, Patience, TestContext.Current.CancellationToken);

        // The waiter registered under the lock before this runs (WaitAsync's synchronous prefix,
        // up to and including adding itself to _waiters, has already completed by the time the
        // Task object is returned — only the `await` below is asynchronous).
        signal.Increment();

        (await waiter).ShouldBeTrue();
    }

    [Fact]
    public async Task Increment_CrossingMultipleWaitersAtOnce_WakesAllOfThem()
    {
        var signal = new TickSignal();
        var waiterAt1 = signal.WaitAsync(1, Patience, TestContext.Current.CancellationToken);
        var waiterAt2 = signal.WaitAsync(2, Patience, TestContext.Current.CancellationToken);
        var waiterAt3 = signal.WaitAsync(3, Patience, TestContext.Current.CancellationToken);

        signal.Increment();
        signal.Increment();
        signal.Increment();

        (await waiterAt1).ShouldBeTrue();
        (await waiterAt2).ShouldBeTrue();
        (await waiterAt3).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitAsync_TimeoutBeforeTargetReached_ReturnsFalse()
    {
        var signal = new TickSignal();

        var reached = await signal.WaitAsync(1, TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        reached.ShouldBeFalse();
        signal.Count.ShouldBe(0);
    }

    [Fact]
    public async Task WaitAsync_CancelledBeforeTargetReached_ReturnsFalse()
    {
        var signal = new TickSignal();
        using var cts = new CancellationTokenSource();

        var waiter = signal.WaitAsync(1, Patience, cts.Token);
        await cts.CancelAsync();

        (await waiter).ShouldBeFalse();
    }

    /// <summary>A waiter that timed out must not still be woken (spuriously "true") by a later increment that crosses its target — a real caller only awaits the first result.</summary>
    [Fact]
    public async Task WaitAsync_AfterTimeout_ALaterIncrementDoesNotChangeTheAlreadyReturnedResult()
    {
        var signal = new TickSignal();

        var reached = await signal.WaitAsync(1, TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);
        reached.ShouldBeFalse();

        signal.Increment();

        signal.Count.ShouldBe(1);
    }

    /// <summary>
    ///     The lost-wakeup race: a waiter registering for `Count+1` and an `Increment` call that
    ///     crosses that exact target, forced to start at the same instant with a Barrier. Without
    ///     `_gate` serializing "check-then-register" against "increment-then-notify", the increment
    ///     can land between the waiter's count check and its registration, and the waiter then hangs
    ///     until the timeout. Repeated to give a lost wakeup many chances to happen.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public async Task WaitAsync_RacedAgainstTheCrossingIncrement_NeverLosesTheWakeup(int run)
    {
        var signal = new TickSignal();
        using var start = new Barrier(2);

        var waiterTask = Task.Run(async () =>
        {
            start.SignalAndWait(Patience);
            return await signal.WaitAsync(1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        var incrementerTask = Task.Run(() =>
        {
            start.SignalAndWait(Patience);
            signal.Increment();
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(waiterTask, incrementerTask).WaitAsync(Patience, TestContext.Current.CancellationToken);

        (await waiterTask).ShouldBeTrue($"run {run}: the waiter must never miss an increment that races its registration");
    }

    /// <summary>
    ///     Many threads incrementing concurrently under real contention: Count must equal the exact
    ///     number of increments, never less (a torn/lost update under the lock). Dedicated
    ///     <see cref="Thread" />s, not <c>Task.Run</c> — the thread pool throttles how fast it grows
    ///     past its minimum, so 50 blocking <c>Barrier.SignalAndWait</c> calls queued onto the pool
    ///     can starve each other and never actually rendezvous within the test's patience window.
    /// </summary>
    [Fact]
    public void Increment_ManyConcurrentCallers_CountEqualsTheExactTotal()
    {
        var signal = new TickSignal();
        const int callers = 50;
        using var start = new Barrier(callers);

        var threads = new Thread[callers];
        for (var i = 0; i < callers; i++)
        {
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait(Patience);
                signal.Increment();
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(Patience).ShouldBeTrue("every incrementer thread must finish within the patience window");
        }

        signal.Count.ShouldBe(callers);
    }

    /// <summary>Several waiters with different targets plus concurrent incrementers hammering the same signal — every waiter whose target is eventually reached must wake with true, and Count must land exactly on the total. Dedicated <see cref="Thread" />s for the same reason as above.</summary>
    [Fact]
    public void Increment_ManyCallersWithSeveralWaiters_EveryReachableWaiterWakesTrue_CountIsExact()
    {
        var signal = new TickSignal();
        const int increments = 40;
        var targets = new[] { 5, 15, 25, 35, 40 };
        using var start = new Barrier(increments + targets.Length);

        var waiterResults = new bool[targets.Length];
        var waiterThreads = new Thread[targets.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            var slot = i;
            waiterThreads[slot] = new Thread(() =>
            {
                start.SignalAndWait(Patience);
                waiterResults[slot] = signal
                    .WaitAsync(targets[slot], TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult();
            });
            waiterThreads[slot].Start();
        }

        var incrementThreads = new Thread[increments];
        for (var i = 0; i < increments; i++)
        {
            incrementThreads[i] = new Thread(() =>
            {
                start.SignalAndWait(Patience);
                signal.Increment();
            });
            incrementThreads[i].Start();
        }

        foreach (var thread in incrementThreads)
        {
            thread.Join(Patience).ShouldBeTrue("every incrementer thread must finish within the patience window");
        }

        foreach (var thread in waiterThreads)
        {
            thread.Join(Patience).ShouldBeTrue("every waiter thread must finish within the patience window");
        }

        waiterResults.ShouldAllBe(reached => reached);
        signal.Count.ShouldBe(increments);
    }
}
