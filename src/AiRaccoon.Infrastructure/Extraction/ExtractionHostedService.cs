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
public sealed partial class ExtractionHostedService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly SharedExtractionRunner _extraction;
    private readonly IPromotionQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationTelemetry _telemetry;
    private readonly ILogger<ExtractionHostedService> _logger;

    /// <summary>Span name and `operation` tag of one extraction pass.</summary>
    internal const string OperationName = "extraction.pass";

    public ExtractionHostedService(IMemoryStore store, SharedExtractionRunner extraction,
        IPromotionQueue queue, TimeProvider timeProvider, IOperationTelemetry telemetry,
        ILogger<ExtractionHostedService> logger)
    {
        _store = store;
        _extraction = extraction;
        _queue = queue;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            _timeProvider);
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
                Log.RunFailed(_logger, ex);
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
            Log.IntervalReadFailed(_logger, ex);
            return TimeSpan.FromMinutes(ExtractionConfigKeys.DefaultIntervalMinutes);
        }
    }

    /// <summary>One extraction pass: enabled check, per-project propose/promote. Test seam.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var pass = _telemetry.Begin(OperationName);
        try
        {
            await RunPassAsync(pass, cancellationToken).ConfigureAwait(false);
            pass.Succeeded();
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

    private async Task RunPassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        // Default 30-minute cadence (WP13 span-volume fix): low-volume enough that spanning every
        // pass costs nothing, so every pass is worth reading rather than distinguishing on candidates.
        pass.NoteWork();
        var enabled = ExtractionConfigKeys.ParseEnabled(
            await _store.GetSettingAsync(ExtractionConfigKeys.EnabledGlobal, cancellationToken)
                .ConfigureAwait(false));
        if (!enabled)
        {
            Log.Skipped(_logger);
            return;
        }

        var mode = ExtractionConfigKeys.ParseMode(
            await _store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken)
                .ConfigureAwait(false));
        var projects = await _store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            Log.NoProjects(_logger);
            return;
        }

        var sharedIndex = await _store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
        var promotedTotal = 0;
        foreach (var projectId in projects)
        {
            try
            {
                if (mode == ExtractMode.Promote)
                {
                    // Promote-from-queue: the propose tier is the source of truth.
                    var outcome = await _queue
                        .PromoteAsync([projectId], SharedExtractionService.DefaultCandidateLimit, cancellationToken)
                        .ConfigureAwait(false);
                    promotedTotal += outcome.PromotedHashes.Count;
                    var candidateCount = outcome.PromotedHashes.Count + outcome.SkippedDuplicates
                        + outcome.Failures.Count;
                    Log.Pass(_logger, projectId, mode, candidateCount, outcome.PromotedHashes.Count);
                    if (outcome.Failures.Count > 0)
                    {
                        Log.PromoteFailures(_logger, projectId, outcome.Failures.Count);
                        foreach (var failure in outcome.Failures)
                        {
                            Log.PromoteFailureDetail(_logger, projectId, failure.Hash, failure.Reason);
                        }
                    }

                    continue;
                }

                var candidates = await _extraction.ProposeAsync(projectId, sharedIndex,
                        includeTtlRows: false, SharedExtractionService.DefaultCandidateLimit, cancellationToken)
                    .ConfigureAwait(false);

                // Debug-only, preview-free review surface: candidate counts are metered, not
                // logged at a default level.
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    Log.Candidate(_logger, i + 1, projectId, candidate.Hash, candidate.Path,
                        string.Join(", ", candidate.Reasons));
                }

                Log.Pass(_logger, projectId, mode, candidates.Count, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown: no per-project failure noise, no doomed round-trips (S5)
            }
            catch (Exception ex)
            {
                Log.ProjectFailed(_logger, projectId, ex);
            }
        }

        pass.Tag("projects", projects.Count.ToString());
        pass.Tag("promoted", promotedTotal.ToString());
        Log.RunCompleted(_logger, projects.Count, promotedTotal);
    }

    private async Task<TimeSpan> ReadIntervalAsync(CancellationToken cancellationToken)
    {
        var minutes = ExtractionConfigKeys.ParseIntervalMinutes(
            await _store.GetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, cancellationToken)
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
            Message = "Extraction pass for {ProjectId} ({Mode}): {Candidates} candidates, {Promoted} promoted")]
        public static partial void Pass(ILogger logger, string projectId, ExtractMode mode, int candidates,
            int promoted);

        [LoggerMessage(EventId = 503, Level = LogLevel.Warning,
            Message = "Extraction pass failed for {ProjectId}")]
        public static partial void ProjectFailed(ILogger logger, string projectId, Exception exception);

        /// <summary>No content preview (data-leak risk) — hash/path/reasons only, and Debug-only:
        /// candidate counts are metered (ai_raccoon.queue.queued), not logged, at Information.</summary>
        [LoggerMessage(EventId = 507, Level = LogLevel.Debug,
            Message = "Extraction candidate #{Rank} for {ProjectId}: {Hash} {Path} ({Reasons})")]
        public static partial void Candidate(ILogger logger, int rank, string projectId,
            string hash, string path, string reasons);

        [LoggerMessage(EventId = 504, Level = LogLevel.Information,
            Message = "Extraction pass complete: {Projects} projects, {Promoted} promoted")]
        public static partial void RunCompleted(ILogger logger, int projects, int promoted);

        [LoggerMessage(EventId = 505, Level = LogLevel.Error, Message = "Extraction run failed")]
        public static partial void RunFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 506, Level = LogLevel.Warning,
            Message = "Extraction interval read failed; falling back to the default")]
        public static partial void IntervalReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 508, Level = LogLevel.Warning,
            Message = "Promote for {ProjectId} had {Count} candidate failure(s); see the debug log for hashes")]
        public static partial void PromoteFailures(ILogger logger, string projectId, int count);

        [LoggerMessage(EventId = 509, Level = LogLevel.Debug,
            Message = "Promote failure for {ProjectId}: {Hash} ({Reason})")]
        public static partial void PromoteFailureDetail(ILogger logger, string projectId, string hash,
            string reason);
    }
}
