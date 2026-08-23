using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Optional capability (WP3, #477): a job that can report how many rows are still outstanding
///     after it runs. Deliberately not on <see cref="IMaintenanceJob" /> — most jobs (vacuum,
///     retention, backfill) have nothing to count, and forcing an answer from all ten
///     implementations was rejected (route (a), docs/work/2026-08-23-post-delta-4-plan.md §Re-review).
///     A job opts in by implementing this alongside <see cref="IMaintenanceJob" /> —
///     <see cref="PendingEmbedJob" /> and <see cref="CodeReindexJob" /> do (#477 names both); the
///     other eight have nothing to count.
/// </summary>
public interface IReportsOutstandingRows
{
    ValueTask<long> CountOutstandingRowsAsync(SqliteConnection connection, CancellationToken cancellationToken);
}
