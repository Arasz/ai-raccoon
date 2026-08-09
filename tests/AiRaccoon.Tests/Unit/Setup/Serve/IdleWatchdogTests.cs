using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

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

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

        public List<string> Projects { get; } = ["acme", "beta"];

        public int ExtractionCalls { get; private set; }

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Projects);

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default)
        {
            ExtractionCalls++;
            return Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);
        }

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.GetValueOrDefault(key));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    [Fact]
    public async Task ExecuteAsync_BeforeTimeout_DoesNotStopTheHost()
    {
        // Baseline: a fresh watchdog lives a full timeout with zero activity.
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromHours(4), lifetime, TestTelemetry.None,
            NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = watchdog.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken); // timer registers
        time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        lifetime.StopCalls.ShouldBe(0);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_PastTimeout_StopsExactlyOnce()
    {
        // The 60s tick cap (docs/plans/2026-08-06-http-serve-mode-plan.md): a 4h timeout's deadline
        // lands exactly on a tick, which is not past it; the first tick strictly past it fires.
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromHours(4), lifetime, TestTelemetry.None,
            NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = watchdog.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(4));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_ShortTimeout_TicksAtQuarterTimeout_NotSixtySeconds()
    {
        // R2 (docs/plans/2026-08-06-http-serve-mode-plan.md): tick = min(60s, timeout/4); for a 2s
        // timeout the tick is 0.5s, so 2.4s must not fire but 2.5s must.
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromSeconds(2), lifetime, TestTelemetry.None,
            NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = watchdog.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1)); // t0+2s: exactly the deadline — not past
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(0.4)); // t0+2.4s: no tick due before 2.5s
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(0.1)); // t0+2.5s: the 0.5s tick fires past the deadline
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task NotifyActivity_ResetsTheTimer()
    {
        var time = new FakeTimeProvider(FixedNow);
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromSeconds(2), lifetime, TestTelemetry.None,
            NullLogger<IdleWatchdog>.Instance);

        using var cts = new CancellationTokenSource();
        var run = watchdog.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        watchdog.NotifyActivity(); // reset the deadline to t0+1s

        time.Advance(TimeSpan.FromSeconds(1)); // t0+2s: past the original deadline
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(1.5)); // t0+3.5s: 2.5s past the reset
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_ExtractionPasses_DoNotResetTheWatchdog()
    {
        // R10 (docs/plans/2026-08-06-http-serve-mode-plan.md): background passes are not activity,
        // so the extraction pass at t0+1min leaves the deadline at t0+4min and the t0+5min tick still fires.
        var time = new FakeTimeProvider(FixedNow);
        var store = new FakeStore();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.IntervalMinutesGlobal] = "1";
        var lifetime = new FakeLifetime();
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromMinutes(4), lifetime, TestTelemetry.None,
            NullLogger<IdleWatchdog>.Instance);
        using var extraction = new ExtractionHostedService(store,
            new SharedExtractionRunner(store, new SharedExtractionService(), new FakePromotionQueue(), time),
            new FakePromotionQueue(), time, TestTelemetry.None, NullLogger<ExtractionHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var watchdogRun = watchdog.StartAsync(cts.Token);
        var extractionRun = extraction.StartAsync(cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1)); // extraction pass + watchdog tick
        await Task.Delay(100, TestContext.Current.CancellationToken);
        store.ExtractionCalls.ShouldBeGreaterThan(0);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromMinutes(3)); // t0+4min: exactly the deadline
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(0);

        time.Advance(TimeSpan.FromMinutes(1)); // t0+5min: a full tick past the deadline
        await Task.Delay(100, TestContext.Current.CancellationToken);
        lifetime.StopCalls.ShouldBe(1);

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
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromHours(4), lifetime, probe.Telemetry,
            NullLogger<IdleWatchdog>.Instance);

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
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromSeconds(2), lifetime, probe.Telemetry,
            NullLogger<IdleWatchdog>.Instance);
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
        using var watchdog = new IdleWatchdog(time, TimeSpan.FromSeconds(2), lifetime, probe.Telemetry,
            NullLogger<IdleWatchdog>.Instance);
        time.Advance(TimeSpan.FromSeconds(5));

        watchdog.RunOnce().ShouldBeFalse();

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(nameof(InvalidOperationException));
    }
}
