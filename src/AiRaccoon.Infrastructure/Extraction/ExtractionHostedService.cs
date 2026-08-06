using AiRaccoon.Core.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Extraction;

/// <summary>
///     Background shared-extraction loop: while enabled, every interval it checks each
///     project's committed memories and extracts the shared-worthy ones — propose mode logs
///     ranked candidates, promote mode shares them (dedup against the existing shared tier,
///     idempotent, never a delete). Off by default; mode and interval come from the settings
///     table (CLI-only config channel). Best-effort: one project's failure never aborts the run.
/// </summary>
public sealed partial class ExtractionHostedService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly SharedExtractionService _extraction;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExtractionHostedService> _logger;
    private const int CandidateLimit = 20;

    public ExtractionHostedService(IMemoryStore store, SharedExtractionService extraction,
        TimeProvider timeProvider, ILogger<ExtractionHostedService> logger)
    {
        _store = store;
        _extraction = extraction;
        _timeProvider = timeProvider;
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
                var rows = await _store.ExtractCandidatesAsync(projectId, includeTtlRows: false, cancellationToken)
                    .ConfigureAwait(false);
                var result = _extraction.Run(mode, projectId, projects, rows,
                    sharedIndex.Values, sharedIndex.Paths, includeTtlRows: false, CandidateLimit,
                    _timeProvider.GetUtcNow());
                foreach (var hash in result.PromotedHashes)
                {
                    await _store.ShareAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
                }

                // Refresh per project: a promotion changes the shared tier, and the next
                // project's dedup must see it (cross-project duplicate rows within one pass).
                if (result.PromotedHashes.Count > 0)
                {
                    sharedIndex = await _store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
                }

                promotedTotal += result.PromotedHashes.Count;
                Log.Pass(_logger, projectId, mode, result.Candidates.Count, result.PromotedHashes.Count);
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

        [LoggerMessage(EventId = 502, Level = LogLevel.Information,
            Message = "Extraction pass for {ProjectId} ({Mode}): {Candidates} candidates, {Promoted} promoted")]
        public static partial void Pass(ILogger logger, string projectId, ExtractMode mode, int candidates,
            int promoted);

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
    }
}
