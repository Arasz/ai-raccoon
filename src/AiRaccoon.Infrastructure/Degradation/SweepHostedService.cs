using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Degradation;

/// <summary>
///     The reaper: on its cadence it sweeps every project's committed entries against the stored
///     rating threshold and deletes the expired ones (see docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.15).
/// </summary>
public sealed partial class SweepHostedService : BackgroundService
{
    /// <summary>Never a setting: a dry-run knob would turn the reaper into a no-op. The kill switch is sweep.enabled.</summary>
    private const bool DryRun = false;

    internal const string OperationName = "sweep.reaper";

    private readonly IMemoryStore _store;
    private readonly SweepService _sweeper;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationTelemetry _telemetry;
    private readonly ILogger<SweepHostedService> _logger;

    /// <summary>Completed after each sweep pass; test seam.</summary>
    internal TickSignal Ticks { get; } = new();

    /// <summary>Completed once the periodic timer is armed; test seam (there is no startup pass).</summary>
    internal TickSignal TimerArmed { get; } = new();

    /// <summary>Completed after each post-tick interval re-read; test seam.</summary>
    internal TickSignal IntervalReReads { get; } = new();

    public SweepHostedService(IMemoryStore store, SweepService sweeper, TimeProvider timeProvider,
        IOperationTelemetry telemetry, ILogger<SweepHostedService> logger)
    {
        _store = store;
        _sweeper = sweeper;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No startup pass: nothing is due at process start, and the first tick is the first sweep.
        using var timer = new PeriodicTimer(await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            _timeProvider);
        TimerArmed.Increment();
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
            finally
            {
                Ticks.Increment();
            }

            // Re-read the interval so config changes apply without a restart.
            timer.Period = await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false);
            IntervalReReads.Increment();
        }
    }

    /// <summary>One sweep pass: kill-switch check, then a per-project sweep. Test seam.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var pass = _telemetry.Begin(OperationName);
        try
        {
            await RunPassAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        var enabled = SweepConfigKeys.ParseEnabled(
            await _store.GetSettingAsync(SweepConfigKeys.EnabledGlobal, cancellationToken).ConfigureAwait(false));
        if (!enabled)
        {
            Log.Skipped(_logger);
            return;
        }

        // Read the threshold directly: ForgettingPolicyService enforces a caller's access mode,
        // and a timer has no caller.
        var threshold = SweepThreshold.Parse(
            await _store.GetSettingAsync(SweepThreshold.SettingKey, cancellationToken).ConfigureAwait(false));

        var projects = await _store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            Log.NoProjects(_logger);
            return;
        }

        var deletedTotal = 0;
        foreach (var projectId in projects)
        {
            try
            {
                var outcome = await _sweeper.SweepAsync(projectId, threshold, DryRun, cancellationToken)
                    .ConfigureAwait(false);
                deletedTotal += LogDeletions(projectId, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.ProjectFailed(_logger, projectId, ex);
            }
        }

        Log.RunCompleted(_logger, projects.Count, deletedTotal);
    }

    /// <summary>Names every deleted hash and its rating — never the entry value — and returns the count.</summary>
    private int LogDeletions(string projectId, SweepOutcome outcome)
    {
        var deleted = outcome.DeletedHashes.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in outcome.Candidates.Where(c => deleted.Contains(c.Hash)))
        {
            Log.Deleted(_logger, projectId, candidate.Hash, candidate.Rating, candidate.AgeDays);
        }

        return deleted.Count;
    }

    /// <summary>Interval from settings with a default fallback: a store failure must not kill the loop.</summary>
    private async Task<TimeSpan> ReadIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hours = SweepConfigKeys.ParseIntervalHours(
                await _store.GetSettingAsync(SweepConfigKeys.IntervalHoursGlobal, cancellationToken)
                    .ConfigureAwait(false));
            return TimeSpan.FromHours(hours);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(_logger, ex);
            return TimeSpan.FromHours(SweepConfigKeys.DefaultIntervalHours);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 520, Level = LogLevel.Debug, Message = "Sweep disabled; skipping")]
        public static partial void Skipped(ILogger logger);

        [LoggerMessage(EventId = 521, Level = LogLevel.Debug, Message = "No projects in the bank; skipping")]
        public static partial void NoProjects(ILogger logger);

        /// <summary>Hash, rating and age only — never the entry value, which the reaper has just destroyed.</summary>
        [LoggerMessage(EventId = 522, Level = LogLevel.Information,
            Message = "Swept {Hash} from {ProjectId} (rating {Rating}, age {AgeDays} days)")]
        public static partial void Deleted(ILogger logger, string projectId, string hash, double rating,
            double ageDays);

        [LoggerMessage(EventId = 523, Level = LogLevel.Warning, Message = "Sweep pass failed for {ProjectId}")]
        public static partial void ProjectFailed(ILogger logger, string projectId, Exception exception);

        [LoggerMessage(EventId = 524, Level = LogLevel.Information,
            Message = "Sweep pass complete: {Projects} projects, {Deleted} entries deleted")]
        public static partial void RunCompleted(ILogger logger, int projects, int deleted);

        [LoggerMessage(EventId = 525, Level = LogLevel.Error, Message = "Sweep run failed")]
        public static partial void RunFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 526, Level = LogLevel.Warning,
            Message = "Sweep interval read failed; falling back to the default")]
        public static partial void IntervalReadFailed(ILogger logger, Exception exception);
    }
}
