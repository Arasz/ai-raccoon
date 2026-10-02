using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackgroundLoopTests
{
    private sealed class RecordingTelemetry : IOperationTelemetry, IOperationScope
    {
        public List<string> Events { get; } = [];

        public IOperationScope Begin(string operation)
        {
            Events.Add($"begin:{operation}");
            return this;
        }

        public void Tag(string key, string value) => Events.Add($"tag:{key}");
        public void NoteWork() => Events.Add("work");
        public void RecordRows(long rows) => Events.Add("rows");
        public void Succeeded() => Events.Add("succeeded");
        public void Failed(Exception exception) => Events.Add($"failed:{exception.Message}");
        public void PartiallyFailed(int failureCount) => Events.Add($"partial:{failureCount}");
        public void Dispose() => Events.Add("dispose");
    }

    [Fact]
    public async Task RunPass_Success_RecordsSucceeded()
    {
        var telemetry = new RecordingTelemetry();

        await telemetry.RunPassAsync("op", (_, _) => Task.CompletedTask, CancellationToken.None);

        telemetry.Events.ShouldBe(["begin:op", "succeeded", "dispose"]);
    }

    [Fact]
    public async Task RunPass_Throws_RecordsFailedAndRethrows()
    {
        var telemetry = new RecordingTelemetry();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            telemetry.RunPassAsync("op", (_, _) => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        thrown.Message.ShouldBe("boom");
        telemetry.Events.ShouldBe(["begin:op", "failed:boom", "dispose"]);
    }

    [Fact]
    public async Task RunPass_CancelledByShutdown_IsAbandonedNotFailed()
    {
        var telemetry = new RecordingTelemetry();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            telemetry.RunPassAsync("op", (_, ct) => Task.FromCanceled(ct), cts.Token));

        telemetry.Events.ShouldBe(["begin:op", "dispose"]);
    }

    [Fact]
    public async Task RunPass_OperationCanceledWithoutShutdown_IsFailed()
    {
        var telemetry = new RecordingTelemetry();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            telemetry.RunPassAsync("op", (_, _) => throw new OperationCanceledException("inner"),
                CancellationToken.None));

        telemetry.Events.ShouldBe(["begin:op", "failed:inner", "dispose"]);
    }

    [Theory]
    [InlineData(0, "succeeded")]
    [InlineData(3, "partial:3")]
    public async Task RunPassCountingFailures_MapsFailureCountToOutcome(int failures, string outcome)
    {
        var telemetry = new RecordingTelemetry();

        await telemetry.RunPassCountingFailuresAsync("op", (_, _) => Task.FromResult(failures),
            CancellationToken.None);

        telemetry.Events.ShouldBe(["begin:op", outcome, "dispose"]);
    }

    [Fact]
    public async Task RunTicks_FailedPassIsReportedAndLoopContinues_ThenCancellationEndsIt()
    {
        var time = new FakeTimeProvider();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var passes = 0;

        var loop = timer.RunTicksAsync(
            _ =>
            {
                passes++;
                if (passes == 1)
                {
                    throw new InvalidOperationException("first");
                }

                return Task.CompletedTask;
            },
            ex => log.Add($"error:{ex.Message}"),
            cts.Token,
            () => log.Add("completed"),
            _ =>
            {
                log.Add("read");
                return Task.FromResult(TimeSpan.FromMinutes(2));
            },
            () => log.Add("reread"));

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => log.Count >= 4);
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitForAsync(() => log.Count >= 7);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => loop); // cancelled while awaiting a tick: propagates, as before

        log.ShouldBe(["error:first", "completed", "read", "reread", "completed", "read", "reread"]);
    }

    [Fact]
    public async Task RunTicks_CancelledMidPass_StopsWithoutReportingError_StillSignalsCompletion()
    {
        var time = new FakeTimeProvider();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        using var cts = new CancellationTokenSource();
        var log = new List<string>();

        var loop = timer.RunTicksAsync(
            async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
            _ => log.Add("error"),
            cts.Token,
            () => log.Add("completed"),
            _ =>
            {
                log.Add("read");
                return Task.FromResult(TimeSpan.FromMinutes(1));
            });

        time.Advance(TimeSpan.FromMinutes(1));
        await loop;

        log.ShouldBe(["completed"]);
    }

    [Fact]
    public async Task RunTicks_WithoutReadInterval_KeepsPeriod()
    {
        var time = new FakeTimeProvider();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        using var cts = new CancellationTokenSource();
        var passes = 0;

        var loop = timer.RunTicksAsync(_ =>
        {
            passes++;
            return Task.CompletedTask;
        }, _ => { }, cts.Token);

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => passes == 1);
        timer.Period.ShouldBe(TimeSpan.FromMinutes(1));
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => loop); // cancelled while awaiting a tick: propagates, as before
    }

    [Fact]
    public async Task ReadOrFallback_ReturnsValue_WhenReadSucceeds()
    {
        var value = await BackgroundLoop.ReadOrFallbackAsync(_ => Task.FromResult(7), 1,
            _ => throw new InvalidOperationException("unexpected"), CancellationToken.None);

        value.ShouldBe(7);
    }

    [Fact]
    public async Task ReadOrFallback_ReportsAndFallsBack_WhenReadThrows()
    {
        Exception? reported = null;

        var value = await BackgroundLoop.ReadOrFallbackAsync<int>(
            _ => throw new InvalidOperationException("down"), 1, ex => reported = ex, CancellationToken.None);

        value.ShouldBe(1);
        reported.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ReadOrFallback_Shutdown_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reported = false;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            BackgroundLoop.ReadOrFallbackAsync<int>(ct => Task.FromCanceled<int>(ct), 1, _ => reported = true,
                cts.Token));

        reported.ShouldBeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(10);
        }
    }
}
