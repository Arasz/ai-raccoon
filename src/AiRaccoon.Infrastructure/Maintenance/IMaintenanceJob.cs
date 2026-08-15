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

    /// <summary>How often to run, or null to run exactly once per bank, ever.</summary>
    TimeSpan? Interval { get; }

    Task RunAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
