using System.Diagnostics;
using AiRaccoon.Core.Observability;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using InstallWatchdog = AiRaccoon.Hosting.Watchdog.InstallWatchdog;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Install watchdog contract (ADR-0116): a `dotnet tool update -g ai-raccoon` deletes the
///     install this backend started from out from under it; the watchdog notices on its own poll
///     and shuts the host down cleanly instead of leaving the maintenance loop to spam 527 forever.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class InstallWatchdogTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private const string InstallDirectory = "/fake/install/dir";

    [Fact]
    public async Task ExecuteAsync_InstallDirectoryPresent_DoesNotStopTheHost_OverSeveralTicks()
    {
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        using var watchdog = new InstallWatchdog(InstallDirectory, () => true, TimeSpan.FromSeconds(20),
            lifetime, ticks, time, NullLogger());

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(20));
        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(20));
        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(20));

        lifetime.StopCalls.ShouldBe(0);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_InstallDirectoryGoesMissing_StopsExactlyOnce()
    {
        var time = new ObservableTime(FixedNow);
        var lifetime = new FakeLifetime();
        var ticks = new TickCounter();
        var exists = true;
        using var watchdog = new InstallWatchdog(InstallDirectory, () => exists, TimeSpan.FromSeconds(20),
            lifetime, ticks, time, NullLogger());

        using var cts = new CancellationTokenSource();
        var run = await StartAndArmAsync(watchdog, time, cts.Token);

        await AdvanceAsync(time, ticks, TimeSpan.FromSeconds(20));
        lifetime.StopCalls.ShouldBe(0, "the directory still exists at the first tick");

        exists = false;
        time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => lifetime.StopCalls == 1, "the first tick after the install directory disappears");

        // The loop returns once it stops the host — a further advance must not stop it a second time.
        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public void RunOnce_InstallDirectoryPresent_DoesNotStop_AndEmitsNoSpan()
    {
        using var probe = new BackgroundTelemetryProbe(InstallWatchdog.OperationName);
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new InstallWatchdog(InstallDirectory, () => true, TimeSpan.FromSeconds(20),
            lifetime, probe.Telemetry, time, NullLogger());

        watchdog.RunOnce().ShouldBeFalse();

        lifetime.StopCalls.ShouldBe(0);
        probe.Spans.ShouldBeEmpty();
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void RunOnce_InstallDirectoryMissing_StopsTheHost_AndLogsOneClearWarning()
    {
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        var logger = new FakeLogger<InstallWatchdog>();
        using var watchdog = new InstallWatchdog(InstallDirectory, () => false, TimeSpan.FromSeconds(20),
            lifetime, TestTelemetry.None, time, logger);

        watchdog.RunOnce().ShouldBeTrue();

        lifetime.StopCalls.ShouldBe(1);
        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain(InstallDirectory);
        record.Message.ShouldContain("dotnet tool update");
        logger.Collector.Count.ShouldBe(1, "exactly one warning, never a repeat per tick");
    }

    [Fact]
    public void RunOnce_WhenTheProbeThrows_RecordsTheFailure_AndDoesNotStop()
    {
        using var probe = new BackgroundTelemetryProbe(InstallWatchdog.OperationName);
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new InstallWatchdog(InstallDirectory,
            () => throw new IOException("cannot stat the install directory"), TimeSpan.FromSeconds(20),
            lifetime, probe.Telemetry, time, NullLogger());

        watchdog.RunOnce().ShouldBeFalse();

        lifetime.StopCalls.ShouldBe(0);
        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("error");
    }

    private static ILogger<InstallWatchdog> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<InstallWatchdog>.Instance;

    /// <summary>Waits for a condition instead of a fixed sleep (WP19, docs/adr/0062) — see IdleWatchdogTests.</summary>
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

    private static async Task AdvanceAsync(ObservableTime time, TickCounter ticks, TimeSpan by)
    {
        var before = ticks.Count;
        time.Advance(by);
        await WaitUntilAsync(() => ticks.Count > before,
            $"a check after advancing {by} (saw {ticks.Count}, wanted more than {before})");
    }

    private static async Task<Task> StartAndArmAsync(InstallWatchdog watchdog, ObservableTime time,
        CancellationToken cancellationToken)
    {
        var run = watchdog.StartAsync(cancellationToken);
        await WaitUntilAsync(() => time.TimersCreated > 0, "the watchdog to register its periodic timer");
        return run;
    }

    /// <summary>A fake clock that reports when a timer has been registered against it (see IdleWatchdogTests).</summary>
    private sealed class ObservableTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timersCreated);
            return timer;
        }
    }

    /// <summary>Counts the watchdog's checks so a test can wait for the loop rather than for the clock.</summary>
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

        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() => StopCalls++;
    }
}
