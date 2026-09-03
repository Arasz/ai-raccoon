using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Core.Memory;

/// <summary>Which repair pass a repair_requests row names.</summary>
public enum RepairKind
{
    Reingest,
    ChunkIndex,
    ProjectIds
}

/// <summary>The repair_requests row key each <see cref="RepairKind" /> maps to.</summary>
public static class RepairKinds
{
    public const string Reingest = "reingest";
    public const string ChunkIndex = "chunk-index";
    public const string ProjectIds = "project-ids";

    extension(RepairKind kind)
    {
        public string ToKey() =>
            kind switch
            {
                RepairKind.Reingest => Reingest,
                RepairKind.ChunkIndex => ChunkIndex,
                RepairKind.ProjectIds => ProjectIds,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "ai-raccoon: unknown repair kind")
            };
    }
}

/// <summary>
///     Reaches `repair` entirely through the server (ADR-0075 amendment): the CLI is a thin client
///     for both halves — a read-only report (<see cref="ReportReingestAsync" />/
///     <see cref="ReportChunkIndexAsync" />, scanned server-side, never touching the bank from the
///     CLI process) and the write, which the CLI can only *request*
///     (<see cref="RequestRepairAsync" />, an outbox row mirroring <see cref="IModelMigrationStore" />
///     /ADR-0076) — the maintenance loop's on-demand job applies it. Split out of
///     <see cref="IMemoryStore" /> for the same reason <see cref="ISettingsStore" /> was.
///     <para>
///         The project-ids report (<see cref="ReportProjectIdsAsync" />) is the P1 census served
///         over the same resource: clusters, orphans and zero-entry rows per id, scanned
///         server-side read-only — Doctor stays read-only and never serves it.
///     </para>
/// </summary>
public interface IRepairStore
{
    Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default);

    Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default);

    Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default);

    Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default);
}
