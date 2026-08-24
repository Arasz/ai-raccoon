using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     One unit of bank maintenance. Scheduling is the runner's business (ADR-0070); a job only
///     declares how often it wants to run and does the work.
/// </summary>
public interface IMaintenanceJob
{
    /// <summary>Ledger key. Stable across releases — renaming one re-runs a job that already ran.</summary>
    string Name { get; }

    /// <summary>What to call this in a log line a human reads. Free to change; <see cref="Name" /> is not.</summary>
    string DisplayName { get; }

    /// <summary>How often to run, or null to run exactly once per bank, ever — unless <see cref="HasWorkAsync" /> overrides that (an on-demand job).</summary>
    TimeSpan? Interval { get; }

    /// <summary>
    ///     Runs, returning true when it created rows that still need embedding. Only then does the
    ///     pass sweep again: vacuum and reclaim never make work, and an unconditional extra sweep
    ///     doubles the window in which a background embed races an in-flight write.
    /// </summary>
    ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken);

    /// <summary>
    ///     On-demand due-ness: true when the job has outstanding work right now, independent of
    ///     <see cref="Interval" />/<c>maintenance_jobs.last_run_at</c> — the runner stamps that
    ///     ledger on every success, so a clock-gated on-demand job would run once and never fire
    ///     again. Default false: every cadence-based job (vacuum, backfill, retention) is unaffected.
    ///     Called every pass, so it must be cheap (one indexed read).
    /// </summary>
    ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
