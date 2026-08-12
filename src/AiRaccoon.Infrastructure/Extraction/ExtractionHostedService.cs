using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Extraction;

/// <summary>
///     Background shared-extraction loop: while enabled, every interval it proposes or promotes
///     each project's committed memories to the shared tier (dedup, idempotent, never delete).
///     Off by default, settings-driven, and best-effort per project.
/// </summary>
public sealed partial class ExtractionHostedService(
    IMemoryStore store,
    SharedExtractionRunner extraction,
    IPromotionQueue queue,
    TimeProvider timeProvider,
    IOperationTelemetry telemetry,
    ILogger<ExtractionHostedService> logger)
    : BackgroundService
{
    /// <summary>Span name and `operation` tag of one extraction pass.</summary>
    internal const string OperationName = "extraction.pass";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.RunFailed(logger, ex);
            }

            // Re-read the interval so config changes apply without a restart.
            timer.Period = await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Interval from settings with a default fallback: a store failure must not kill the loop.</summary>
    private async Task<TimeSpan> ReadIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadIntervalAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(logger, ex);
            return TimeSpan.FromMinutes(ExtractionConfigKeys.DefaultIntervalMinutes);
        }
    }

    /// <summary>One extraction pass: enabled check, per-project propose/promote. Test seam.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var pass = telemetry.Begin(OperationName);
        try
        {
            var failures = await RunPassAsync(pass, cancellationToken).ConfigureAwait(false);
            if (failures > 0)
            {
                pass.PartiallyFailed(failures);
            }
            else
            {
                pass.Succeeded();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown cut the pass short: abandoned, not failed
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            throw;
        }
    }

    /// <summary>
    ///     Runs the pass and returns the number of projects whose propose/promote threw
    ///     (H8): RunOnceAsync uses this to decide Succeeded vs PartiallyFailed.
    /// </summary>
    private async Task<int> RunPassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        // Default 30-minute cadence (WP13 span-volume fix): low-volume enough that spanning every
        // pass costs nothing, so every pass is worth reading rather than distinguishing on candidates.
        pass.NoteWork();
        var enabled = ExtractionConfigKeys.ParseEnabled(
            await store.GetSettingAsync(ExtractionConfigKeys.EnabledGlobal, cancellationToken)
                .ConfigureAwait(false));
        if (!enabled)
        {
            Log.Skipped(logger);
            return 0;
        }

        var mode = ExtractionConfigKeys.ParseMode(
            await store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken)
                .ConfigureAwait(false));
        var projects = await store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            Log.NoProjects(logger);
            return 0;
        }

        var sharedIndex = await store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
        var promotedTotal = 0;
        var failures = 0;
        foreach (var projectId in projects)
        {
            try
            {
                if (mode == ExtractMode.Promote)
                {
                    // Promote-from-queue: the propose tier is the source of truth.
                    var outcome = await queue
                        .PromoteAsync([projectId], SharedExtractionService.DefaultCandidateLimit, cancellationToken)
                        .ConfigureAwait(false);
                    promotedTotal += outcome.PromotedHashes.Count;
                    var candidateCount = outcome.PromotedHashes.Count + outcome.Absorbed
                                                                      + outcome.SkippedDuplicates + outcome.Failures.Count;
                    Log.Pass(logger, projectId, mode, candidateCount, outcome.PromotedHashes.Count,
                        outcome.Absorbed);
                    if (outcome.Failures.Count > 0)
                    {
                        // One summary per batch; the per-failure detail lives in the outcome,
                        // and the failure count is metered (ai_raccoon.queue.promote_failures).
                        Log.PromoteFailures(logger, projectId, outcome.Failures.Count);
                    }

                    continue;
                }

                var candidates = await extraction.ProposeAsync(projectId, sharedIndex,
                        false, SharedExtractionService.DefaultCandidateLimit, cancellationToken)
                    .ConfigureAwait(false);

                // Per-pass summary only; candidate counts are metered, not logged per row.
                Log.Pass(logger, projectId, mode, candidates.Count, 0, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown: no per-project failure noise, no doomed round-trips (S5)
            }
            catch (Exception ex)
            {
                Log.ProjectFailed(logger, projectId, ex);
                failures++;
            }
        }

        pass.Tag("projects", projects.Count.ToString());
        pass.Tag("promoted", promotedTotal.ToString());
        // "failures" is tagged by PartiallyFailed (RunOnceAsync), not here — one place sets it.
        Log.RunCompleted(logger, projects.Count, promotedTotal);
        return failures;
    }

    private async Task<TimeSpan> ReadIntervalAsync(CancellationToken cancellationToken)
    {
        var minutes = ExtractionConfigKeys.ParseIntervalMinutes(
            await store.GetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, cancellationToken)
                .ConfigureAwait(false));
        return TimeSpan.FromMinutes(minutes);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 500, Level = LogLevel.Debug, Message = "Shared extraction disabled; skipping")]
        public static partial void Skipped(ILogger logger);

        [LoggerMessage(EventId = 501, Level = LogLevel.Debug, Message = "No projects in the bank; skipping")]
        public static partial void NoProjects(ILogger logger);

        [LoggerMessage(EventId = 502, Level = LogLevel.Debug,
            Message = "Extraction pass for {ProjectId} ({Mode}): {Candidates} claimed, {Promoted} promoted, {Absorbed} absorbed")]
        public static partial void Pass(ILogger logger, string projectId, ExtractMode mode, int candidates,
            int promoted, int absorbed);

        [LoggerMessage(EventId = 503, Level = LogLevel.Warning,
            Message = "Extraction pass failed for {ProjectId}")]
        public static partial void ProjectFailed(ILogger logger, string projectId, Exception exception);

        [LoggerMessage(EventId = 504, Level = LogLevel.Information,
            Message = "Extraction pass complete: {Projects} projects, {Promoted} promoted")]
        public static partial void RunCompleted(ILogger logger, int projects, int promoted);

        [LoggerMessage(EventId = 505, Level = LogLevel.Error, Message = "Extraction run failed")]
        public static partial void RunFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 506, Level = LogLevel.Warning,
            Message = "Extraction interval read failed; falling back to the default")]
        public static partial void IntervalReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 508, Level = LogLevel.Warning,
            Message = "Promote for {ProjectId} had {Count} candidate failure(s); details are in the outcome")]
        public static partial void PromoteFailures(ILogger logger, string projectId, int count);
    }
}
