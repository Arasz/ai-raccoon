using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>What one pass of the runner did, per job.</summary>
public sealed record MaintenanceJobOutcome(string Name, bool Ran, string? Error, bool CreatedWork = false);

/// <summary>
///     Runs the due maintenance jobs and records each run in the bank (ADR-0070).
///     <para>
///         The ledger is in the bank rather than in the process on purpose. The clock it replaces was
///         a field seeded on first run, so a bank only ever opened by short-lived processes never
///         reached any interval at all — measured: a 183 MB bank held 42 MB of free pages that no
///         VACUUM would ever collect.
///     </para>
/// </summary>
public sealed partial class MaintenanceJobRunner(
    TimeProvider timeProvider,
    IMeasurementRecorder measurements,
    ILogger<MaintenanceJobRunner> logger)
{
    public async Task<IReadOnlyList<MaintenanceJobOutcome>> RunDueAsync(SqliteConnection connection,
        IReadOnlyList<IMaintenanceJob> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobs);

        var now = timeProvider.GetUtcNow();
        var outcomes = new List<MaintenanceJobOutcome>(jobs.Count);
        foreach (var job in jobs)
        {
            bool due;
            try
            {
                if (job is VacuumJob vacuum)
                {
                    // The only job whose cadence is user-configurable; read it before asking for
                    // Interval. Reads the same connection as the ledger SELECT below, so it shares
                    // the same guard: a broken settings read must not stop the pass either.
                    await vacuum.RefreshIntervalAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                var lastRun = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                        "SELECT last_run_at FROM maintenance_jobs WHERE name = @name",
                        new { name = job.Name }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);

                // On-demand due-ness (ADR-0076) is independent of the clock-gated IsDue check below —
                // it exists precisely so a job like ModelMigrationJob isn't run-once by the ledger stamp
                // every success writes. Checked first since it is the cheaper of the two most passes.
                due = await job.HasWorkAsync(connection, cancellationToken).ConfigureAwait(false)
                      || IsDue(job, lastRun, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken due-ness check must not stop the pass: it happened before the
                // run try/catch below, so an uncaught throw here used to skip every job
                // registered after it. No ledger write happens either way, so it retries
                // next pass — same semantics as a failed RunAsync.
                Log.JobCheckFailed(logger, job.DisplayName, job.Name, ex);
                outcomes.Add(new MaintenanceJobOutcome(job.Name, false, ex.Message));
                continue;
            }

            if (!due)
            {
                outcomes.Add(new MaintenanceJobOutcome(job.Name, false, null));
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            bool createdWork;
            try
            {
                createdWork = await job.RunAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's failure must not stop the rest, and must not stamp the ledger: an
                // unstamped job retries next pass, which is the behaviour a transient lock wants.
                Log.JobFailed(logger, job.DisplayName, job.Name, ex);
                outcomes.Add(new MaintenanceJobOutcome(job.Name, false, ex.Message));
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO maintenance_jobs (name, last_run_at, run_count) VALUES (@name, @now, 1)
                    ON CONFLICT(name) DO UPDATE SET last_run_at = @now, run_count = run_count + 1
                    """,
                    new { name = job.Name, now = now.ToUnixTimeSeconds() }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.JobRan(logger, job.DisplayName, job.Name, elapsedMs);
            await RecordJobMetricsAsync(connection, job, elapsedMs, now, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new MaintenanceJobOutcome(job.Name, true, null, createdWork));
        }

        return outcomes;
    }

    /// <summary>
    ///     Route (a) (WP3, #477): the duration is data the runner already computes for the log line
    ///     above, so recording it costs nothing extra. The rows gauge only fires for a job that opts
    ///     into <see cref="IReportsOutstandingRows" /> — none of the ten do today — so a job with
    ///     nothing to count records duration alone, never a fabricated zero.
    /// </summary>
    private async Task RecordJobMetricsAsync(SqliteConnection connection, IMaintenanceJob job, double elapsedMs,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        measurements.Record(new Measurement(MetricsConfigKeys.JobDurationMetricName(job.Name),
            MeasurementKind.Histogram, elapsedMs, "ms", now, MetricsConfigKeys.SelfMetricsProjectId));

        if (job is not IReportsOutstandingRows counter)
        {
            return;
        }

        try
        {
            var outstanding = await counter.CountOutstandingRowsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            measurements.Record(new Measurement(MetricsConfigKeys.JobRowsMetricName(job.Name),
                MeasurementKind.Gauge, outstanding, "count", now, MetricsConfigKeys.SelfMetricsProjectId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The job itself already ran and is already ledger-stamped above: a broken row count
            // must not undo that or retry a job that succeeded. It just means this pass's rows
            // gauge is missing, not zero.
            Log.JobRowsCountFailed(logger, job.DisplayName, job.Name, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 525, Level = LogLevel.Information,
            Message = "ai-raccoon: maintenance job '{DisplayName}' ({JobName}) ran in {ElapsedMs:F0} ms")]
        public static partial void JobRan(ILogger logger, string displayName, string jobName, double elapsedMs);

        [LoggerMessage(EventId = 526, Level = LogLevel.Warning,
            Message = "ai-raccoon: maintenance job '{DisplayName}' ({JobName}) failed; it will be retried")]
        public static partial void JobFailed(ILogger logger, string displayName, string jobName, Exception exception);

        [LoggerMessage(EventId = 527, Level = LogLevel.Warning,
            Message = "ai-raccoon: maintenance job '{DisplayName}' ({JobName}) due-check failed; it will be retried")]
        public static partial void JobCheckFailed(ILogger logger, string displayName, string jobName, Exception exception);

        [LoggerMessage(EventId = 528, Level = LogLevel.Warning,
            Message = "ai-raccoon: maintenance job '{DisplayName}' ({JobName}) outstanding-rows count failed; the job itself already ran, only its rows gauge is missing this pass")]
        public static partial void JobRowsCountFailed(ILogger logger, string displayName, string jobName, Exception exception);
    }

    private static bool IsDue(IMaintenanceJob job, long? lastRunAt, DateTimeOffset now)
    {
        if (lastRunAt is null)
        {
            return true;
        }

        // A run-once job that has a row has run. This is the whole reason the ledger stores a
        // timestamp rather than a boolean: the same table answers both questions.
        return job.Interval is { } interval
               && now - DateTimeOffset.FromUnixTimeSeconds(lastRunAt.Value) >= interval;
    }
}
