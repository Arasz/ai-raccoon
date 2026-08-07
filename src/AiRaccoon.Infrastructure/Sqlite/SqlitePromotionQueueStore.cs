using System.Text.Json;
using AiRaccoon.Core.Memory;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Propose-tier persistence in the memory.db promotion_queue table; never synced (waiting rows are per-machine by design).</summary>
public sealed class SqlitePromotionQueueStore(
    SqliteConnectionFactory factory,
    TimeProvider timeProvider) : IPromotionQueueStore
{
    public async Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // ON CONFLICT DO UPDATE reports a changed row for both an insert and a conflict-update
        // (SQLite changes() counts both), so the affected-row sum is not a queue-size delta.
        // A pre-upsert snapshot of which hashes already exist is the simplest way to tell them
        // apart: only the ones absent here are genuinely new.
        var hashes = rows.Select(r => r.Hash).Distinct(StringComparer.Ordinal).ToList();
        var existing = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    PromotionQueueSql.ExistingHashes,
                    new { ProjectId = projectId, Hashes = hashes },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    PromotionQueueSql.Upsert,
                    new
                    {
                        ProjectId = projectId,
                        row.Hash,
                        row.Path,
                        row.Value,
                        row.SourceFile,
                        row.Score,
                        Reasons = JsonSerializer.Serialize(row.Reasons),
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return hashes.Count(h => !existing.Contains(h));
    }

    public async Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PromotionQueueRowRow>(
                new CommandDefinition(
                    PromotionQueueSql.List,
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.Select(ToRow).ToList();
    }

    public async Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var removed = await connection.QueryAsync<PromotionQueueRowRow>(
                new CommandDefinition(
                    PromotionQueueSql.Discard,
                    new { ProjectId = projectId, Hash = hash },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return removed.Select(ToRow).ToList();
    }

    public async Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var total = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT count(*) FROM promotion_queue",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var avgWait = await connection.ExecuteScalarAsync<double?>(
                new CommandDefinition(
                    "SELECT CAST(avg(@Now - created_at) AS REAL) FROM promotion_queue",
                    new { Now = now },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        // Dynamic query: an empty GROUP BY yields no rows, and Dapper still builds a typed
        // deserializer from the NULL-typed count(*) column — the dynamic path materializes
        // per row and skips that entirely.
        var perProject = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in await connection.QueryAsync(
                     new CommandDefinition(PromotionQueueSql.StatsPerProject,
                         cancellationToken: cancellationToken)).ConfigureAwait(false))
        {
            perProject[(string)row.ProjectId] = (int)(long)row.Count;
        }

        return new PromotionQueueStats(total, avgWait, perProject);
    }

    public async Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var victim = await connection.QuerySingleOrDefaultAsync<PromotionQueueRowRow>(
                new CommandDefinition(
                    PromotionQueueSql.EvictVictim,
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return victim is null ? null : ToRow(victim);
    }

    private static PromotionQueueRow ToRow(PromotionQueueRowRow row) =>
        new(row.ProjectId, row.Hash, row.Path, row.Value, row.SourceFile, row.Score,
            ParseReasons(row.Reasons), row.CreatedAt, row.UpdatedAt);

    private static IReadOnlyList<string> ParseReasons(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Dapper mapping target: TEXT columns map to string, REAL to double, INTEGER to long.
    private sealed record PromotionQueueRowRow(
        string ProjectId, string Hash, string Path, string Value, string? SourceFile,
        double Score, string? Reasons, long CreatedAt, long UpdatedAt);
}
