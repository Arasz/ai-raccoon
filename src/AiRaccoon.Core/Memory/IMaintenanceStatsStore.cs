namespace AiRaccoon.Core.Memory;

/// <summary>
///     Reaches `settings maintenance list`'s live bank disk stats entirely through the server
///     (ADR-0075 amendment): a read-only, server-computed report — the CLI never opens the bank for
///     it, unconditionally, since this verb has no --apply gate to begin with. Split out alongside
///     <see cref="IRepairStore" /> for the same reason.
/// </summary>
public interface IMaintenanceStatsStore
{
    Task<BankStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
