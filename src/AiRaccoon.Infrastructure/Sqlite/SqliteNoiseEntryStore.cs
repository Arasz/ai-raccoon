using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using CommunityToolkit.Diagnostics;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>INoiseEntryStore over noise_entries (ADR-0029/ADR-0039).</summary>
public sealed class SqliteNoiseEntryStore(ISqliteConnectionFactory factory) : INoiseEntryStore
{
    public async Task RecordAsync(MemoryWriteRequest request, string policyName, long expiresAtUnixSeconds,
        long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(request);
        Guard.IsNotNullOrWhiteSpace(policyName);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
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
                    }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Policy, int Count)>(
                new CommandDefinition(NoiseEntrySql.CountByPolicy, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var byPolicy = rows.ToDictionary(r => r.Policy, r => r.Count, StringComparer.Ordinal);
        return new NoiseEntrySummary(byPolicy.Values.Sum(), byPolicy);
    }

    public async Task<IReadOnlyList<NoiseEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<NoiseEntry>(
                new CommandDefinition(NoiseEntrySql.SelectRecent, new { Limit = limit }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<int> PurgeExpiredAsync(long nowUnixSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(NoiseEntrySql.DeleteExpired, new { Now = nowUnixSeconds }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
