using System.Text.Json;
using AiRaccoon.Core.Memory;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>One-shot orphan report for `ai-raccoon extract prune` (ADR-0023): rows PruneOrphansAsync found (and, with apply, removed) per project.</summary>
public sealed record PromotionQueueOrphanReport(int TotalOrphans, IReadOnlyDictionary<string, int> PerProject);

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
                        row.ScorerVersion,
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
        // The single stalest row's age: an average hides one very old row once enough fresh
        // rows join the same queue, and nothing else drains a propose-only queue.
        var oldestWait = await connection.ExecuteScalarAsync<double?>(
                new CommandDefinition(
                    "SELECT CAST(max(@Now - created_at) AS REAL) FROM promotion_queue",
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

        return new PromotionQueueStats(total, avgWait, perProject, oldestWait);
    }

    public async Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        // Dynamic query: over an empty queue the aggregates are untyped NULLs, and Dapper cannot
        // build a typed deserializer from those — the dynamic path materializes the row instead.
        var row = await connection.QuerySingleAsync(
                new CommandDefinition(
                    PromotionQueueSql.WaitStats,
                    new { Now = now, ProjectId = projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return new PromotionWaitStats(
            (int)(long)row.WaitingCount,
            (double?)row.AvgWaitSeconds,
            (double?)row.OldestWaitSeconds,
            (int)(long)row.OccupyingProjects);
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

    public async Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(
                    PromotionQueueSql.ClearStale,
                    new { ProjectId = projectId, CurrentVersion = currentScorerVersion },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Rows whose backing entries row was deleted before the promotion_queue_entries_ad
    ///     trigger existed (ADR-0023) — the trigger covers everything from here on; this is the
    ///     one-shot catch-up for a bank that already accumulated dead rows. Reports without
    ///     deleting unless <paramref name="apply"/> is true; idempotent either way (a second call
    ///     after apply finds nothing left to report). Count and delete run inside one transaction
    ///     so nothing can change the queue between them, and the reported total is the DELETE's own
    ///     affected-row count, not the earlier count query's snapshot.
    /// </summary>
    public async Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Dynamic query: an orphan-free queue yields no rows, and Dapper cannot build a typed
        // deserializer from an empty result — the dynamic path materializes per row instead.
        var perProject = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in await connection.QueryAsync(
                     new CommandDefinition(PromotionQueueSql.OrphanCountsPerProject,
                         transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
        {
            perProject[(string)row.ProjectId] = (int)(long)row.Count;
        }

        if (!apply)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PromotionQueueOrphanReport(perProject.Values.Sum(), perProject);
        }

        var removed = await connection.ExecuteAsync(
                new CommandDefinition(PromotionQueueSql.DeleteOrphans,
                    transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PromotionQueueOrphanReport(removed, perProject);
    }

    private static PromotionQueueRow ToRow(PromotionQueueRowRow row) =>
        new(row.ProjectId, row.Hash, row.Path, row.Value, row.SourceFile, row.Score,
            ParseReasons(row.Reasons), row.CreatedAt, row.UpdatedAt, (int)row.ScorerVersion);

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
        double Score, string? Reasons, long CreatedAt, long UpdatedAt, long ScorerVersion);
}
