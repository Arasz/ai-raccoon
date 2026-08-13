using AiRaccoon.Core.Access;
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
public sealed partial class SweepHostedService(
    IMemoryStore store,
    ISweepService sweeper,
    TimeProvider timeProvider,
    IOperationTelemetry telemetry,
    ILogger<SweepHostedService> logger)
    : BackgroundService
{
    /// <summary>Never a setting: a dry-run knob would turn the reaper into a no-op. The kill switch is sweep.enabled.</summary>
    private const bool DryRun = false;

    internal const string OperationName = "sweep.reaper";

    /// <summary>Completed after each sweep pass; test seam.</summary>
    internal TickSignal Ticks { get; } = new();

    /// <summary>Completed once the periodic timer is armed; test seam (there is no startup pass).</summary>
    internal TickSignal TimerArmed { get; } = new();

    /// <summary>Completed after each post-tick interval re-read; test seam.</summary>
    internal TickSignal IntervalReReads { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No startup pass: nothing is due at process start, and the first tick is the first sweep.
        using var timer = new PeriodicTimer(await ReadIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            timeProvider);
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
                Log.RunFailed(logger, ex);
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
    ///     Runs the pass and returns the number of projects whose sweep threw. Test seam
    ///     for RunOnceAsync's outcome decision (H8).
    /// </summary>
    private async Task<int> RunPassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        var enabled = SweepConfigKeys.ParseEnabled(
            await store.GetSettingAsync(SweepConfigKeys.EnabledGlobal, cancellationToken).ConfigureAwait(false));
        if (!enabled)
        {
            Log.Skipped(logger);
            return 0;
        }

        // Read the threshold directly: it is a bank-global setting (ForgettingPolicyService's
        // per-project Destructive gate exists for the caller's *consent to change it*, not for
        // reading it) and a timer has no caller to gate in the first place.
        var threshold = SweepThreshold.Parse(
            await store.GetSettingAsync(SweepThreshold.SettingKey, cancellationToken).ConfigureAwait(false));

        var projects = await store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            Log.NoProjects(logger);
            return 0;
        }

        var deletedTotal = 0;
        var failures = 0;
        foreach (var projectId in projects)
        {
            try
            {
                // H6: the same Destructive requirement memory_sweep enforces at the MCP boundary
                // (full mode only) applies here — a project that has not consented to destructive
                // operations does not lose that consent just because the caller is a timer.
                var mode = await ResolveModeAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (mode != AccessMode.Full)
                {
                    Log.SkippedMode(logger, projectId, mode);
                    continue;
                }

                var outcome = await sweeper.SweepAsync(projectId, threshold, DryRun, cancellationToken)
                    .ConfigureAwait(false);
                deletedTotal += LogDeletions(projectId, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.ProjectFailed(logger, projectId, ex);
                failures++;
            }
        }

        if (deletedTotal > 0)
        {
            // A pass that deleted user data is always worth reading, unlike an empty pass
            // (WP13 span-volume fix): NoteWork only fires here, never unconditionally.
            pass.NoteWork();
            pass.Tag("deleted", deletedTotal.ToString());
        }

        Log.RunCompleted(logger, projects.Count, deletedTotal);
        return failures;
    }

    /// <summary>
    ///     Resolves one project's effective access mode: per-project setting, else global
    ///     default, else rw (mirrors AiRaccoon.Access.MemoryAccessGuard.ResolveAsync's rule via the
    ///     same Core policy, without a project reference back to the host layer — clean layering).
    ///     Never throws for an unparsable value; propagates a genuine store failure to the caller's
    ///     per-project catch, which counts it as this project's failure rather than aborting the pass.
    /// </summary>
    private async Task<AccessMode> ResolveModeAsync(string projectId, CancellationToken cancellationToken)
    {
        var perProjectRaw = await store
            .GetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), cancellationToken)
            .ConfigureAwait(false);
        if (AccessModePolicy.Parse(perProjectRaw) is { } perProject)
        {
            return perProject;
        }

        var globalRaw = await store.GetSettingAsync(AccessModePolicy.GlobalSettingKey, cancellationToken)
            .ConfigureAwait(false);
        return AccessModePolicy.Resolve(AccessModePolicy.Parse(globalRaw), null);
    }

    /// <summary>Names every deleted hash and its rating — never the entry value — and returns the count.</summary>
    private int LogDeletions(string projectId, SweepOutcome outcome)
    {
        var deleted = outcome.DeletedHashes.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in outcome.Candidates.Where(c => deleted.Contains(c.Hash)))
        {
            Log.Deleted(logger, projectId, candidate.Hash, candidate.Rating, candidate.AgeDays);
        }

        return deleted.Count;
    }

    /// <summary>Interval from settings with a default fallback: a store failure must not kill the loop.</summary>
    private async Task<TimeSpan> ReadIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hours = SweepConfigKeys.ParseIntervalHours(
                await store.GetSettingAsync(SweepConfigKeys.IntervalHoursGlobal, cancellationToken)
                    .ConfigureAwait(false));
            return TimeSpan.FromHours(hours);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(logger, ex);
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

        /// <summary>H6: a project outside full access mode is skipped, not reaped.</summary>
        [LoggerMessage(EventId = 527, Level = LogLevel.Debug,
            Message = "Sweep skipped {ProjectId}: access mode {Mode} has not consented to destructive sweeps")]
        public static partial void SkippedMode(ILogger logger, string projectId, AccessMode mode);
    }
}
