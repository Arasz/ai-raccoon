using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     The relay half of a CLI-requested project-ids repair (air-merge P2, ADR-0075 amendment,
///     mirrors <see cref="ReingestRepairJob" />/<see cref="ChunkIndexRepairJob" />):
///     <c>repair project-ids --apply</c> commits a repair_requests row through the server instead of
///     writing the bank itself; this job applies it. On-demand only — <see cref="HasWorkAsync" />
///     reads the request row, never a clock, and <see cref="RunAsync" /> re-checks the open request
///     itself before touching anything: the runner also invokes RunAsync on the IsDue first pass
///     (missing ledger row), and that pass must stay a no-op — the dropped path permanently deletes
///     rows, so folding without a human <c>--apply</c> would contradict the CLI contract (review
///     SHOULD-1, gating decision: gate on open request). Ordered steps: derive the
///     <see cref="ProjectIdsFoldPlan" /> from a live census, rewrite every id-keyed surface
///     (<see cref="ProjectIdsRepair" />), re-derive chunk positions under the winner groups
///     (<see cref="ChunkIndexRepair" />), then mark the request finished — renamed rows are left
///     pending for PendingEmbedJob, ordered after this in the job list. The run stamps the
///     maintenance_jobs ledger like every job, which is NOT the P3 enforcement gate's migration
///     marker (the runner stamps it after every RunAsync call, including gated no-ops) — P3 gates
///     on the repair_requests finished row for this kind, stamped below only when a requested run
///     completes.
/// </summary>
public sealed class ProjectIdsRepairJob(
    IFileTypeMatcher fileTypeMatcher,
    IEmbeddingService embeddingService,
    TimeProvider timeProvider)
    : IMaintenanceJob
{
    public const string JobName = "repair-project-ids";

    public string Name => JobName;

    public string DisplayName => "apply a CLI-requested project-ids repair";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(MemorySql.HasOpenRepairRequest,
                new { kind = RepairKinds.ProjectIds }, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;

    /// <summary>Folds with the request's stored one-shot map (null/empty means the empty map), re-derives chunk positions, then marks the request finished. No open request means no work: returns false without touching the bank. The plan is derived from a live census at apply time — never a replay of the CLI's dry-run plan (concurrent writes between diagnose and apply re-plan).</summary>
    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var open = await connection.ExecuteScalarAsync<long>(new CommandDefinition(MemorySql.HasOpenRepairRequest,
                    new { kind = RepairKinds.ProjectIds }, cancellationToken: cancellationToken))
                .ConfigureAwait(false) > 0;
        if (!open)
        {
            return false;
        }

        var mapJson = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(MemorySql.SelectOpenRepairMapJson,
                    new { kind = RepairKinds.ProjectIds }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        var map = ResolveMap(mapJson);
        var plan = ProjectIdsFoldPlan.FromCensus(
            await ProjectIdCensus.CollectAsync(connection, cancellationToken).ConfigureAwait(false),
            map);
        var createdWork = false;
        if (!plan.IsEmpty)
        {
            var result = await new ProjectIdsRepair(timeProvider)
                .ApplyAsync(connection, plan, cancellationToken).ConfigureAwait(false);
            createdWork = result.TotalChanges > 0;
        }

        var chunks = await new ChunkIndexRepair(fileTypeMatcher, embeddingService)
            .RunAsync(connection, true, cancellationToken).ConfigureAwait(false);
        createdWork = createdWork || chunks.RowsRepositioned > 0 || chunks.RowsSetToUnknown > 0;

        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
                new { kind = RepairKinds.ProjectIds, finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return createdWork;
    }

    internal static ProjectIdAliasMap ResolveMap(string? mapJson)
    {
        if (string.IsNullOrWhiteSpace(mapJson))
        {
            return ProjectIdAliasMap.Empty;
        }

        return ProjectIdAliasMap.FromJson(mapJson);
    }
}
