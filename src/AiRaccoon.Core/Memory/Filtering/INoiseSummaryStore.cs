namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     Reaches `noise entries`'s noise_entries summary entirely through the server (ADR-0075
///     amendment): a read-only, server-computed report — the CLI never opens the bank for it,
///     unconditionally, since this verb has no --apply gate to begin with. Split out from
///     <see cref="INoiseEntryStore" /> for the same reason <see cref="IMaintenanceStatsStore" /> is
///     split from the store it reports on.
/// </summary>
public interface INoiseSummaryStore
{
    Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default);
}
