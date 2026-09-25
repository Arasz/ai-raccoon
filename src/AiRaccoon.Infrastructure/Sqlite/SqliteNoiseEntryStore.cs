using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using CommunityToolkit.Diagnostics;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>INoiseEntryStore over noise_entries (ADR-0029/ADR-0039); also the server-side default for
/// <see cref="INoiseSummaryStore" /> (ADR-0075 amendment) — overridden by LazyServerSettingsStore for
/// the CLI graph, same shape as SqliteMaintenanceStatsStore.</summary>
public sealed class SqliteNoiseEntryStore(ISqliteConnectionFactory factory) : INoiseEntryStore, INoiseSummaryStore
{
    public async Task RecordAsync(MemoryWriteRequest request, string policyName, long expiresAtUnixSeconds,
        long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(request);
        Guard.IsNotNullOrWhiteSpace(policyName);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(
                new CommandDefinition(NoiseEntrySql.Insert,
                    new
                    {
                        RequestContent = request.Content,
                        request.ProjectId,
                        request.SourceFile,
                        DetectedByPolicy = policyName,
                        ExpiresAt = expiresAtUnixSeconds,
                        CreatedAt = nowUnixSeconds
                    }, cancellationToken: cancellationToken));
    }

    public async Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<PolicyCount>(
                new CommandDefinition(NoiseEntrySql.CountByPolicy, cancellationToken: cancellationToken));

        var byPolicy = rows.ToDictionary(r => r.Policy, r => r.Count, StringComparer.Ordinal);
        return new NoiseEntrySummary(byPolicy.Values.Sum(), byPolicy);
    }

    public async Task<IReadOnlyList<NoiseEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<NoiseEntry>(
                new CommandDefinition(NoiseEntrySql.SelectRecent, new { Limit = limit }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<int> PurgeExpiredAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        return await connection.ExecuteAsync(
                new CommandDefinition(NoiseEntrySql.DeleteExpired, new { Now = nowUnixSeconds }, cancellationToken: cancellationToken));
    }

    // A plain class, not a record: SQLite's count(*) comes back Int64, and Dapper's constructor
    // matching requires an exact-type match against an `int` — property-set materialization tolerates it.
    private sealed class PolicyCount
    {
        public string Policy { get; set; } = "";

        public int Count { get; set; }
    }
}
