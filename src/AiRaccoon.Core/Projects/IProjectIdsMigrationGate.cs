namespace AiRaccoon.Core.Projects;

/// <summary>
///     The P3 enforcement gate's migration marker (air-merge plan P3, review M1): true once a
///     finished project-ids repair row exists. The marker is the repair_requests finished row for
///     kind=project-ids — never the maintenance_jobs ledger stamp, which the runner writes after
///     every RunAsync call including gated no-ops. Until it flips, enforcement stays a no-op and
///     the bank behaves exactly as before (the canonical winners are themselves
///     orphan-unregistered on an unmigrated bank, so refusing would refuse their own writes).
/// </summary>
public interface IProjectIdsMigrationGate
{
    /// <summary>True when a requested project-ids repair run completed (finished_at stamped).</summary>
    Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default);
}
