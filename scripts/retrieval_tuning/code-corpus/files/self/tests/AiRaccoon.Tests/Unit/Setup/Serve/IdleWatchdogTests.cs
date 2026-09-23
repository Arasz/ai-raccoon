using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using IdleWatchdog = AiRaccoon.Hosting.Watchdog.IdleWatchdog;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Idle watchdog contract: /mcp traffic resets the idle deadline, background passes never do,
///     and the tick period is min(60s, timeout/4) (docs/plans/2026-08-06-http-serve-mode-plan.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IdleWatchdogTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_BeforeTimeout_DoesNotStopTheHost()
    {
        // Baseline: a fresh watchdog lives a full timeout with zero activity.
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new IdleWatchdog(lifetime, ticks,
            TimeSpan.FromHours(4), time, NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        // 3h at a 60s tick is 180 checks; waiting for them is what proves the loop looked and
        // declined, where a fixed sleep would have passed just as happily had it never run.
        await AdvanceAsync(time, ticks, TimeSpan.FromHours(3));

        lifetime.StopCalls.ShouldBe(0);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_PastTimeout_StopsExactlyOnce()
    {
        // The 60s tick cap (docs/plans/2026-08-06-http-serve-mode-plan.md): a 4h timeout's deadline
        // lands exactly on a tick, which is not past it; the first tick strictly past it fires.
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new IdleWatchdog(lifetime, ticks,
            TimeSpan.FromHours(4), time, NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        await AdvanceAsync(time, ticks, TimeSpan.FromHours(4));
        lifetime.StopCalls.ShouldBe(0, "the deadline landed exactly on a tick, which is not past it");

        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => lifetime.StopCalls == 1, "the first tick strictly past the deadline");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_ShortTimeout_TicksAtQuarterTimeout_NotSixtySeconds()
    {
        // R2 (docs/plans/2026-08-06-http-serve-mode-plan.md): tick = min(60s, timeout/4); for a 2s
        // timeout the tick is 0.5s, so 2.4s must not fire but 2.5s must.
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new IdleWatchdog(lifetime, ticks,
            TimeSpan.FromSeconds(2), time, NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(1));
        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(1)); // t0+2s: exactly the deadline — not past
        lifetime.StopCalls.ShouldBe(0);

        // t0+2.4s: no tick is due before 2.5s, so the tick count must NOT move — asserted rather
        // than slept through, which is the whole point of counting them.
        var before = ticks.Count;
        time.Advance(TimeSpan.FromSeconds(0.4));
        ticks.Count.ShouldBe(before, "no tick is due between 2.0s and 2.5s at a 0.5s period");
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(0.1)); // t0+2.5s: the 0.5s tick fires past the deadline
        await WaitUntilAsync(() => lifetime.StopCalls == 1, "the 2.5s tick to fire past the deadline");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task NotifyActivity_ResetsTheTimer()
    {
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new IdleWatchdog(lifetime, ticks,
            TimeSpan.FromSeconds(2), time, NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(1));

        watchdog.NotifyActivity(); // reset the deadline to t0+1s

        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(1)); // t0+2s: past the ORIGINAL deadline
        lifetime.StopCalls.ShouldBe(0, "the reset moved the deadline to t0+3s");

        time.Advance(TimeSpan.FromSeconds(1.5)); // t0+3.5s: 2.5s past the reset
        await WaitUntilAsync(() => lifetime.StopCalls == 1, "a tick past the reset deadline");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_ExtractionPasses_DoNotResetTheWatchdog()
    {
        // R10 (docs/plans/2026-08-06-http-serve-mode-plan.md): background passes are not activity,
        // so the extraction pass at t0+1min leaves the deadline at t0+4min and the t0+5min tick still fires.
        var time = new ObservableTime(FixedNow);
        var store = new FakeStore();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.IntervalMinutesGlobal] = "1";
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new IdleWatchdog(lifetime, ticks,
            TimeSpan.FromMinutes(4), time, NullLogger<IdleWatchdog>.Instance);
        using var extraction = new ExtractionHostedService(store,
            new SharedExtractionRunner(store, new SharedExtractionService(), new FakePromotionQueue(), time),
            new FakePromotionQueue(), time, TestTelemetry.None, NullLogger<ExtractionHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var watchdogRun = watchdog.StartAsync(cts.Token);
        var extractionRun = extraction.StartAsync(cts.Token);
        // BOTH services register a timer against this clock, and an advance before either exists is
        // lost, not merely late — the same race, twice (docs/adr/0062).
        await WaitUntilAsync(() => time.TimersCreated >= 2,
            "the watchdog and the extraction service to register their periodic timers");

        // t0+1min at a 60s tick: one watchdog check, plus the extraction pass on its own interval.
        await AdvanceAsync(time, ticks, TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => store.ExtractionCalls > 0, "the extraction pass to run");
        lifetime.StopCalls.ShouldBe(0);

        await AdvanceAsync(time, ticks, TimeSpan.FromMinutes(3)); // t0+4min: exactly the deadline
        lifetime.StopCalls.ShouldBe(0, "the deadline landed exactly on a tick, which is not past it");

        time.Advance(TimeSpan.FromMinutes(1)); // t0+5min: a full tick past the deadline
        await WaitUntilAsync(() => lifetime.StopCalls == 1, "the first tick past the deadline");

        await cts.CancelAsync();
        await watchdogRun;
        await extractionRun;
    }

    [Fact]
    public void RunOnce_StillWithinTheTimeout_EmitsNoSpan_ButRecordsTheDurationAndCount()
    {
        // The steady-state tick (as often as every 60s by default): still within the timeout, so
        // nothing happens. Counted, never spanned.
        using var probe = new BackgroundTelemetryProbe(IdleWatchdog.OperationName);
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(lifetime, probe.Telemetry,
            TimeSpan.FromHours(4), time, NullLogger<IdleWatchdog>.Instance);

        watchdog.RunOnce().ShouldBeFalse();

        probe.Spans.ShouldBeEmpty();
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void RunOnce_PastTheTimeout_EmitsASpan()
    {
        using var probe = new BackgroundTelemetryProbe(IdleWatchdog.OperationName);
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(lifetime, probe.Telemetry,
            TimeSpan.FromSeconds(2), time, NullLogger<IdleWatchdog>.Instance);
        time.Advance(TimeSpan.FromSeconds(5));

        watchdog.RunOnce().ShouldBeTrue();

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void RunOnce_WhenTheTickThrows_RecordsTheFailure()
    {
        using var probe = new BackgroundTelemetryProbe(IdleWatchdog.OperationName);
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime { StopError = new InvalidOperationException("zephyrone") };
        using var watchdog = new IdleWatchdog(lifetime, probe.Telemetry,
            TimeSpan.FromSeconds(2), time, NullLogger<IdleWatchdog>.Instance);
        time.Advance(TimeSpan.FromSeconds(5));

        watchdog.RunOnce().ShouldBeFalse();

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(nameof(InvalidOperationException));
    }

    /// <summary>
    ///     Waits for the watchdog loop to have observed a clock advance, instead of sleeping a fixed
    ///     real time and hoping. The loop's clock is fake but its continuations run on the real thread
    ///     pool, so "advance, sleep 100 ms, assert" races the scheduler: under full-suite contention
    ///     the continuation may not have run yet, and the assertion sees the pre-advance state
    ///     (WP19, docs/adr/0062). The ceiling is generous because it bounds a failure, not a pass —
    ///     a healthy run exits on the first poll.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"timed out after 15s waiting for {what}");
    }

    /// <summary>
    ///     Advances the fake clock and waits until the loop has observed it. Deliberately waits for
    ///     "at least one more check", not an exact count: PeriodicTimer does not queue missed ticks,
    ///     so a single advance spanning several periods may surface as one tick. What matters is that
    ///     the loop looked after the advance, which is what makes the assertion that follows real.
    /// </summary>
    private static async Task AdvanceAsync(ObservableTime time, TickCounter ticks, TimeSpan by)
    {
        var before = ticks.Count;
        time.Advance(by);
        await WaitUntilAsync(() => ticks.Count > before,
            $"a check after advancing {by} (saw {ticks.Count}, wanted more than {before})");
    }

    /// <summary>
    ///     Starts the watchdog and waits until its PeriodicTimer exists. This is THE race: StartAsync
    ///     returns before ExecuteAsync has created the timer, so a clock advance before that point is
    ///     not merely observed late — it is not observed at all, and the loop then waits for a tick
    ///     that a frozen fake clock will never deliver. The old tests papered over it with a
    ///     `Task.Delay(100); // timer registers`, which is a guess about scheduler latency
    ///     (WP19, docs/adr/0062).
    /// </summary>
    private static async Task<Task> StartAndArmAsync(IdleWatchdog watchdog, ObservableTime time,
        CancellationToken cancellationToken)
    {
        var run = watchdog.StartAsync(cancellationToken);
        await WaitUntilAsync(() => time.TimersCreated > 0, "the watchdog to register its periodic timer");
        return run;
    }

    /// <summary>A fake clock that reports when a timer has been registered against it.</summary>
    private sealed class ObservableTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        /// <summary>
        ///     Counted AFTER the base call, never before. Incrementing first reports "armed" while the
        ///     timer is not yet in the provider's queue, so an advance racing that window is lost —
        ///     the exact ordering bug this class exists to close, and it survived four clean local
        ///     runs before CI's timing exposed it.
        /// </summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timersCreated);
            return timer;
        }
    }

    /// <summary>
    ///     Counts the watchdog's idle checks so a test can wait for the loop rather than for the clock.
    ///     Once the watchdog stops the host its loop returns, so the count stops rising — which is why
    ///     the assertions after a stop wait on StopCalls instead.
    /// </summary>
    private sealed class TickCounter : IOperationTelemetry, IOperationScope
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IOperationScope Begin(string operation)
        {
            Interlocked.Increment(ref _count);
            return this;
        }

        public void Tag(string key, string value)
        {
        }

        public void NoteWork()
        {
        }

        public void RecordRows(long rows)
        {
        }

        public void Succeeded()
        {
        }

        public void Failed(Exception exception)
        {
        }

        public void PartiallyFailed(int failureCount)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }

        /// <summary>Fails the tick from inside the watchdog's own try block.</summary>
        public Exception? StopError { get; set; }

        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication()
        {
            StopCalls++;
            if (StopError is not null)
            {
                throw StopError;
            }
        }
    }

    private sealed class FakeStore : FakeMemoryStore
    {
        public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

        public List<string> Projects { get; } = ["acme", "beta"];

        public int ExtractionCalls { get; private set; }

        public override Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Projects);

        public override Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default)
        {
            ExtractionCalls++;
            return Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);
        }

        public override Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.GetValueOrDefault(key));
    }
}
