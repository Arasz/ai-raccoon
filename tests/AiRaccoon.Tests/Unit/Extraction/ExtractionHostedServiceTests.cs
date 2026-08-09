using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractionHostedServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static (FakeExtractionStore Store, FakeTimeProvider Time, ExtractionHostedService Service,
        FakePromotionQueue Queue) NewStack() =>
        NewStack(NullLogger<ExtractionHostedService>.Instance);

    private static (FakeExtractionStore Store, FakeTimeProvider Time, ExtractionHostedService Service,
        FakePromotionQueue Queue) NewStack(ILogger<ExtractionHostedService> logger,
        IOperationTelemetry? telemetry = null)
    {
        var store = new FakeExtractionStore();
        var time = new FakeTimeProvider(FixedNow);
        var queue = new FakePromotionQueue();
        var service = new ExtractionHostedService(store,
            new SharedExtractionRunner(store, new SharedExtractionService(), queue, time), queue, time,
            telemetry ?? TestTelemetry.None, logger);
        return (store, time, service, queue);
    }

    /// <summary>Polls until the loop has done the thing, instead of betting a fixed sleep against machine load.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("The extraction loop did not reach the expected state in 30s.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>
    ///     Advances until the loop reacts. An advance issued before ExecuteAsync constructs its
    ///     PeriodicTimer is lost, so re-advances until a pass runs; each effective advance yields
    ///     exactly one tick, and polling stops at the first.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan period,
        Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("The extraction loop never ran a pass within 30s.");
            }

            time.Advance(period);

            // Generous next to a pass over in-memory fakes: only re-advance once the loop has
            // plainly not picked up the last one, so a slow pass cannot provoke a second tick.
            for (var i = 0; i < 25 && !condition(); i++)
            {
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private static ExtractionCandidateRow Row(string hash, string? sourceFile = null, string value = "fact",
        int accessCount = 0, double rating = 0.5) =>
        new(hash, $"{hash}.md", value, sourceFile, rating, accessCount, FixedNow.AddDays(-5), null);

    [Fact]
    public async Task RunOnce_Disabled_DoesNothing()
    {
        var (store, _, service, _) = NewStack();
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunOnce_ProposeMode_ListsCandidates_WithoutSharing()
    {
        var (store, _, service, _) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(2); // both projects were scanned; nothing shared
    }

    [Fact]
    public async Task RunOnce_PromoteMode_DelegatesToTheQueue()
    {
        var (store, _, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];
        queue.PromoteOutcome = new PromoteOutcome(["h1"], 1, new Dictionary<string, int>());

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        // Promote-from-queue: the loop no longer shares directly — dedup and sharing
        // live in PromotionQueueService (covered by its own integration tests).
        store.Shared.ShouldBeEmpty();
        queue.PromoteCalls.ShouldBe([new[] { "acme" }, new[] { "beta" }]);
    }

    [Fact]
    public async Task RunOnce_PromoteMode_PassesTheSharedDefaultLimit()
    {
        var (store, _, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = Enumerable.Range(0, 25)
            .Select(i => Row($"h{i:00}", null, $"organic fact number {i}"))
            .ToList();

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        queue.LastLimit.ShouldBe(SharedExtractionService.DefaultCandidateLimit);
    }

    [Fact]
    public async Task RunOnce_PromoteMode_CallsTheQueuePerProject()
    {
        var (store, _, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        // Cross-project dedup lives inside PromotionQueueService (the shared index is
        // refreshed per PromoteAsync there); the loop just delegates per project.
        store.Candidates["acme"] = [Row("h1", null, "identical cross-project fact")];
        store.Candidates["beta"] = [Row("h1", null, "identical cross-project fact")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        queue.PromoteCalls.ShouldBe([new[] { "acme" }, new[] { "beta" }]);
        store.Shared.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunOnce_ProjectError_IsTolerated_OthersStillProcessed()
    {
        var (store, _, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["beta"] = [Row("h9", null, "organic fact about acme")];
        queue.PromoteError = new InvalidOperationException("boom");

        await Should.NotThrowAsync(() =>
            service.RunOnceAsync(TestContext.Current.CancellationToken));

        queue.PromoteCalls.Count.ShouldBe(2, "acme's failure must not abort beta");
    }

    [Fact]
    public async Task RunOnce_NoProjects_NoOp()
    {
        var (store, _, service, _) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Projects.Clear();

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_IntervalReadFailure_DoesNotKillTheLoop()
    {
        var (store, time, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];
        store.IntervalReadError = new InvalidOperationException("db busy");

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        // ExecuteAsync must fall back to the default interval and keep looping,
        // not fault (BackgroundService StopHost would kill the whole server).
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(60), () => queue.PromoteCalls.Count > 0,
            TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_PollLoop_RespectsInterval_AndStopsOnCancellation()
    {
        var (store, time, service, queue) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        // The first advance may land before the timer exists; re-advance until a pass runs.
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(30), () => queue.PromoteCalls.Count > 0,
            TestContext.Current.CancellationToken);
        queue.PromoteCalls.Count.ShouldBe(2); // 2 projects, one pass

        // The timer is demonstrably live now, so one advance is one tick.
        time.Advance(TimeSpan.FromMinutes(30));
        await WaitUntilAsync(() => queue.PromoteCalls.Count >= 4, TestContext.Current.CancellationToken);
        // Second pass proves the loop iterates.
        queue.PromoteCalls.Count.ShouldBe(4);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunOnce_Cancelled_PropagatesOperationCanceled_WithoutProjectFailedLog()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, queue) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        queue.PromoteError = new OperationCanceledException(); // BARE — no token attached

        await Should.ThrowAsync<OperationCanceledException>(() => service.RunOnceAsync(cts.Token));

        queue.PromoteCalls.Count.ShouldBe(1); // aborts at acme; beta never scanned
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 503);
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunOnce_UncancelledOce_IsTolerated_OthersStillProcessed()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, queue) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["beta"] = [Row("h9", null, "organic fact about acme")];
        using var cts = new CancellationTokenSource(); // NOT cancelled
        queue.PromoteError = new OperationCanceledException("boom");

        await Should.NotThrowAsync(() => service.RunOnceAsync(cts.Token));

        queue.PromoteCalls.Count.ShouldBe(2, "beta must still be processed");
        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 503 && r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunOnce_ProposeMode_LogsRankedCandidateDetails()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, _) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] =
        [
            // organic-note + durable-fact-language + foreign-subject: clearly outscores h2.
            Row("h1", null,
                "The retry queue must never drop a message silently; this is a hard invariant recorded " +
                "here so nobody has to relearn it after beta hit the same failure mode independently " +
                "last quarter and traced it back to the same root cause in its own dispatcher."),
            // organic-note only, no rule language or foreign mention.
            Row("h2", null,
                "The retry queue clears completed messages once every consumer has acknowledged them, " +
                "keeping memory bounded during long backlogs without any operator intervention at all " +
                "during normal day to day operation across every environment this service runs in."),
            // doc-index chunk: a hard-noise channel no content can rescue → below floor, excluded
            Row("h3", "docs/README.md",
                "notes about the plan that continue for a little while without stating anything durable " +
                "or citing anything that would justify sharing this beyond the local scratch file it " +
                "already lives in for the rest of the current sprint cycle around here.")
        ];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var records = logger.Collector.GetSnapshot().Where(r => r.Id.Id == 507).ToList();
        records.Count.ShouldBe(2);
        // Debug, not Information: the detail survives for local debugging, but a
        // default-level serve.log no longer carries it (counted by metrics instead).
        records.ShouldAllBe(r => r.Level == LogLevel.Debug);
        records[0].Message.ShouldContain("#1");
        records[0].Message.ShouldContain("acme");
        records[0].Message.ShouldContain("h1");
        records[0].Message.ShouldContain("h1.md");
        records[0].Message.ShouldContain("organic-note");
        records[0].Message.ShouldContain("foreign-subject");
        records[0].Message.ShouldContain("durable-fact-language");
        // The content preview is dropped from the message (a data-leak into logs otherwise).
        records[0].Message.ShouldNotContain("must never drop a message silently");
        // Rank ordering: the pre-sorted-by-score list emits rank one before rank two.
        records[1].Message.ShouldContain("#2");
        // Counts line still emitted alongside the details, also demoted to Debug.
        var passRecords = logger.Collector.GetSnapshot().Where(r => r.Id.Id == 502).ToList();
        passRecords.ShouldNotBeEmpty();
        passRecords.ShouldAllBe(r => r.Level == LogLevel.Debug);
    }

    /// <summary>The per-pass summary is the operator-facing survivor of the quiet-down: unlike
    /// 502/507, it stays at Information so a default-level serve.log still shows the run happened.</summary>
    [Fact]
    public async Task RunOnce_LogsTheRunSummary_AtInformation()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, queue) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];
        queue.PromoteOutcome = new PromoteOutcome(["h1"], 1, new Dictionary<string, int>());

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var summary = logger.Collector.GetSnapshot().Single(r => r.Id.Id == 504);
        summary.Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public async Task RunOnce_PromoteMode_WithFailures_LogsWarningAndDebugDetail()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, queue) = NewStack(logger);
        store.Projects.Clear();
        store.Projects.Add("acme"); // a single project keeps the per-pass log assertions unambiguous
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];
        queue.PromoteOutcome = new PromoteOutcome(["h1"], 0, new Dictionary<string, int>())
        {
            Failures = [new PromoteFailure("acme", "h2", "stale-hash")]
        };

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var warning = logger.Collector.GetSnapshot().Single(r => r.Id.Id == 508);
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("acme");
        warning.Message.ShouldContain("1");

        var detail = logger.Collector.GetSnapshot().Single(r => r.Id.Id == 509);
        detail.Level.ShouldBe(LogLevel.Debug);
        detail.Message.ShouldContain("h2");
        detail.Message.ShouldContain("stale-hash");
    }

    [Fact]
    public async Task RunOnce_PromoteMode_LogsCountsOnly_NoCandidateDetails()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service, _) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 502); // pass ran
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 507); // no candidate details
    }

    [Fact]
    public async Task RunOnce_EmitsASpanAndADurationForThePass()
    {
        using var probe = new BackgroundTelemetryProbe(ExtractionHostedService.OperationName);
        var (store, _, service, _) = NewStack(NullLogger<ExtractionHostedService>.Instance, probe.Telemetry);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public async Task RunOnce_WhenThePassThrows_RecordsTheFailure()
    {
        using var probe = new BackgroundTelemetryProbe(ExtractionHostedService.OperationName);
        var (store, _, service, _) = NewStack(NullLogger<ExtractionHostedService>.Instance, probe.Telemetry);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.ProjectListError = new InvalidOperationException("zephyrone");

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.RunOnceAsync(TestContext.Current.CancellationToken));

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(nameof(InvalidOperationException));
    }

    // ---- H8: a pass with per-project failures must not report result=success ----

    [Fact]
    public async Task RunOnce_AllProjectsThrow_ResultIsNotSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(ExtractionHostedService.OperationName);
        var (store, _, service, queue) = NewStack(NullLogger<ExtractionHostedService>.Instance, probe.Telemetry);
        store.Projects.Clear();
        store.Projects.AddRange(["acme", "beta", "gamma"]);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        // PromoteAsync always throws regardless of project (the fake's one error source),
        // so this exercises "every project in the pass failed".
        queue.PromoteError = new InvalidOperationException("boom");

        await Should.NotThrowAsync(() => service.RunOnceAsync(TestContext.Current.CancellationToken));

        queue.PromoteCalls.Count.ShouldBe(3, "all three projects must still have been attempted");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }

    [Fact]
    public async Task RunOnce_OneOfThreeThrows_OthersStillProcessed_AndTheFailureIsVisible()
    {
        using var probe = new BackgroundTelemetryProbe(ExtractionHostedService.OperationName);
        var (store, _, service, _) = NewStack(NullLogger<ExtractionHostedService>.Instance, probe.Telemetry);
        store.Projects.Clear();
        store.Projects.AddRange(["acme", "beta", "gamma"]);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        // Propose mode (default): the fake only throws ExtractionError for "acme".
        store.ExtractionError = new InvalidOperationException("boom");
        store.Candidates["beta"] = [Row("h1", null, "organic fact about beta")];
        store.Candidates["gamma"] = [Row("h2", null, "organic fact about gamma")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.ExtractionCalls.ShouldBe(3, "beta and gamma must still have been scanned");
        var span = probe.Spans.ShouldHaveSingleItem();
        span.GetTagItem("failures").ShouldNotBeNull();
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }

    [Fact]
    public async Task RunOnce_CleanPass_StillRecordsSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(ExtractionHostedService.OperationName);
        var (store, _, service, _) = NewStack(NullLogger<ExtractionHostedService>.Instance, probe.Telemetry);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] = [Row("h1", null, "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }
}
