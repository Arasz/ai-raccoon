using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Persistence seam for watches + per-file fingerprints (docs/plans/file-watcher-implementation.md S4 unit tests fake this).</summary>
public interface IWatchStore
{
    Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default);

    Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     No-overlapping-watches atomicity, resolved AND committed under one lock
    ///     (docs/work/2026-08-21-code-search-implementation-plan.md §2.2, review codereviewer
    ///     MUST-FIX 7 + TOCTOU close): opens ONE <c>BEGIN IMMEDIATE</c> transaction, reads the
    ///     project's CURRENT watches from inside it, resolves <paramref name="candidate" /> against
    ///     that just-acquired snapshot via <paramref name="overlapResolver" />, then (when accepted)
    ///     prunes the losers (row + cascaded <c>watch_files</c>) and registers the new watch — all
    ///     before <c>COMMIT</c>. Two concurrent callers can never both decide "I win" against the
    ///     same stale pre-lock snapshot: the second caller's own <c>BEGIN IMMEDIATE</c> blocks until
    ///     the first commits, so it can only ever resolve against the first caller's already-durable
    ///     state. The caller updates runtime (<c>WatchPipeline</c>) state AFTER this returns, driven
    ///     by the returned <see cref="WatchOverlapDecision" />.
    /// </summary>
    Task<WatchOverlapDecision> ResolveAndAddAsync(string projectId, WatchOverlapCandidate candidate,
        IWatchOverlapResolver overlapResolver, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default);

    Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default);

    Task<string?> GetFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every fingerprinted file path for the project (catch-up reconciliation).</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string projectId, CancellationToken cancellationToken = default);
}

/// <summary>Dapper impl of IWatchStore over the watches/watch_files tables (MemorySql consts); also
/// the server-side default for <see cref="IWatchRegisteredStore" /> (ADR-0075 amendment) —
/// overridden by LazyServerSettingsStore for the CLI graph, same shape as SqliteMaintenanceStatsStore.</summary>
public sealed class WatchStore(ISqliteConnectionFactory factory) : IWatchStore, IWatchRegisteredStore
{
    public async Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.InsertWatchIfAbsent,
                    new { projectId, path, createdAt, lastChangeTs }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var pathPrefix = LikePattern.Escape(path) + "/%";
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.DeleteWatchFilesByProjectPathCascade,
                        new { projectId, path, pathPrefix }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.DeleteWatch, new { projectId, path },
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<WatchOverlapDecision> ResolveAndAddAsync(string projectId, WatchOverlapCandidate candidate,
        IWatchOverlapResolver overlapResolver, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            // S4 TOCTOU close: read the project's CURRENT watches and resolve the candidate against
            // them from INSIDE the same write-locked transaction that then commits the outcome — a
            // concurrent writer either already committed before this BEGIN IMMEDIATE succeeded (so
            // its watch is visible here) or is still waiting for this transaction's own COMMIT (so
            // it will see THIS decision once it gets its turn). Either way, no two callers can ever
            // resolve against the same stale snapshot.
            var rows = await connection.QueryAsync<WatchRegistration>(
                    new CommandDefinition(MemorySql.SelectWatches, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var existing = rows
                .Where(w => w.ProjectId == projectId)
                .Select(w => new WatchOverlapCandidate(w.Path, w.CreatedAt))
                .ToArray();
            var decision = overlapResolver.Resolve(existing, candidate);

            if (decision.Outcome == WatchOverlapOutcome.Accepted)
            {
                foreach (var pruned in decision.Pruned)
                {
                    var pathPrefix = LikePattern.Escape(pruned.Path) + "/%";
                    await connection.ExecuteAsync(
                            new CommandDefinition(MemorySql.DeleteWatchFilesByProjectPathCascade,
                                new { projectId, path = pruned.Path, pathPrefix }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                    await connection.ExecuteAsync(
                            new CommandDefinition(MemorySql.DeleteWatch, new { projectId, path = pruned.Path },
                                cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }

                await connection.ExecuteAsync(
                        new CommandDefinition(MemorySql.InsertWatchIfAbsent,
                            new
                            {
                                projectId, path = candidate.Path, createdAt = candidate.CreatedAt, lastChangeTs = 0L
                            }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return decision;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<WatchRegistration>(
                new CommandDefinition(MemorySql.SelectWatches, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }

    public async Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpdateWatchLastChange, new { projectId, path, lastChangeTs },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetFileHashAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(MemorySql.SelectWatchFile, new { projectId, path },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpsertWatchFile,
                    new { projectId, path, fileHash, updatedAt }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string>(
                new CommandDefinition(MemorySql.SelectWatchFilesByProject, new { projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return [.. rows];
    }
}
