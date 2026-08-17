namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Reaches `watch registered`'s live watch-registration list entirely through the server
///     (ADR-0075 amendment): a read-only report — the CLI never opens the bank for it. Split out
///     from <see cref="IWatchStore" /> for the same reason <see cref="AiRaccoon.Core.Memory.IMaintenanceStatsStore" />
///     is split from the store it reports on.
/// </summary>
public interface IWatchRegisteredStore
{
    Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default);
}
