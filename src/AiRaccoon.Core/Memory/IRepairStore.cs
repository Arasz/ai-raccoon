using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Core.Memory;

/// <summary>Which repair pass a repair_requests row names.</summary>
public enum RepairKind
{
    Reingest,
    ChunkIndex
}

/// <summary>The repair_requests row key each <see cref="RepairKind" /> maps to.</summary>
public static class RepairKinds
{
    public const string Reingest = "reingest";
    public const string ChunkIndex = "chunk-index";

    extension(RepairKind kind)
    {
        public string ToKey() =>
            kind switch
            {
                RepairKind.Reingest => Reingest,
                RepairKind.ChunkIndex => ChunkIndex,
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
/// </summary>
public interface IRepairStore
{
    Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default);

    Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default);

    Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default);
}
