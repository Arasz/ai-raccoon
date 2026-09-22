using AiRaccoon.Core.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     The delete family: <see cref="DeleteAsync" /> (memory_delete's whole-write reach, N7/F26),
///     the sweep's <see cref="DeleteInScopeAsync" />, <see cref="DeleteContextAsync" />,
///     <see cref="DeleteSourcePathAsync" />, and the tombstone-then-delete core they share
///     (ADR-0035/WP5a). WP8's write/ingest seam
///     (docs/plans/2026-08-14-code-quality-improvement-plan.md), split out for the same reason as
///     <c>SqliteMemoryStore.Rows.cs</c> and <c>SqliteMemoryStore.Search.cs</c>: the store's measured
///     line ratchet (<c>SqliteMemoryStoreSizeRatchetTests</c>) says take a seam rather than raise
///     the cap.
/// </summary>
public sealed partial class SqliteMemoryStore
{
    public async Task<int> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteCoreAsync(connection, projectId, hash, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The sweep's own delete (H2): restricted to the one scope it enumerated, so a
    ///     project-scoped pass cannot also remove a sibling row that shares this hash in another
    ///     scope or workspace — hash alone is not a unique row (ContentHash.Of has no scope
    ///     input). Unlike <see cref="DeleteAsync" />/memory_delete, which targets a hash wherever
    ///     it lives; this is a narrower, internal verb, not a public tool.
    /// </summary>
    public async Task<bool> DeleteInScopeAsync(string projectId, string hash, string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteCoreAsync(connection, projectId, hash, scope, cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        ContextScope.RequireWithinProject(context, projectId);

        var (filter, values) = ContextFilterProvider.For(context, projectId, "");
        var parameters = new DynamicParameters();
        foreach (var (key, value) in values)
        {
            parameters.Add(key, value);
        }
        parameters.Add("deletedAt", timeProvider.GetUtcNow().ToUnixTimeSeconds());

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await InTransactionAsync(connection, async () =>
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.TombstoneFromPredicate(filter), parameters,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM entries WHERE {filter}", parameters,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes every committed chunk of one source path and its subtree (directory delete
    ///     cascades), plus the per-path watch fingerprints and their sync tombstones, in the same
    ///     transaction — a delete-then-recreate cycle must not hash-skip back to stale chunks.
    ///     Watch registration survives.
    /// </summary>
    public async Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var pathPrefix = LikePattern.Escape(path) + "/%";
        var parameters = new
        {
            projectId, path, pathPrefix, deletedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        };
        return await InTransactionAsync(connection, async () =>
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.TombstoneFromPredicate(MemorySql.DeleteBySourcePathPredicate), parameters, cancellationToken))
                .ConfigureAwait(false);
            var deleted = await connection.ExecuteAsync(
                    Def(MemorySql.DeleteBySourcePath, parameters, cancellationToken))
                .ConfigureAwait(false);
            // Code corpus leg (docs/work/2026-08-21-code-search-implementation-plan.md §3.5):
            // unconditional — each ingestor self-filters on re-ingest, so the digest needs no
            // classification here. A no-op for a memory-only path (idx_code_entries_path-backed).
            await connection.ExecuteAsync(
                    Def(MemorySql.DeleteCodeBySourcePath, parameters, cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    Def(MemorySql.DeleteWatchFilesByProjectPathCascade, parameters, cancellationToken))
                .ConfigureAwait(false);
            return deleted;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Entry-delete and sync tombstone in one transaction (ADR-0035/WP5a): a crash between the
    ///     two used to leave the content deleted locally with no tombstone, resurrecting it on the
    ///     next sync. Same BEGIN IMMEDIATE/COMMIT/ROLLBACK shape as <see cref="DeleteSourcePathAsync" />
    ///     and <see cref="SqliteMemoryStore.ReplaceCoreAsync" />; both callers are top-level, so there is no nested-transaction hazard.
    /// </summary>
    private async Task<int> DeleteCoreAsync(SqliteConnection connection, string projectId, string hash,
        string? scope, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            // The whole-write delete (N7/F26) reaches every row sharing the write's path;
            // resolved once so the delete and its tombstone derivation both match on it.
            // DeleteInScopeAsync (scope set) stays hash+scope exact and never needs a path.
            var path = scope is null
                ? await connection.QueryFirstOrDefaultAsync<string?>(
                        Def(MemorySql.SelectPathByHashAndProject, new { hash, projectId }, cancellationToken))
                    .ConfigureAwait(false)
                : null;

            var recomputeContext = await connection.QueryFirstOrDefaultAsync<DeleteRecomputeRow>(
                    Def(MemorySql.SelectDeleteRecomputeContext, new { hash, projectId, scope }, cancellationToken))
                .ConfigureAwait(false);

            // Tombstones every row about to be removed (N7/F26) — not just the reported hash —
            // before the delete runs, from the identical predicate, in the same transaction.
            await connection.ExecuteAsync(
                    Def(MemorySql.TombstoneFromPredicate(MemorySql.DeleteMatchPredicate),
                        new
                        {
                            hash, projectId, scope, path,
                            deletedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
                        },
                        cancellationToken))
                .ConfigureAwait(false);

            var deleted = await connection.ExecuteAsync(
                    Def(MemorySql.DeleteByHashAndProject, new { hash, projectId, scope, path }, cancellationToken))
                .ConfigureAwait(false);

            if (deleted > 0 && recomputeContext?.SourceFile is not null)
            {
                var context = ContextStringOf(recomputeContext.Scope, recomputeContext.ContextLabel,
                    recomputeContext.WorkspaceId, projectId);
                await CompactChunkColumnsAfterDeleteAsync(connection, context, projectId,
                    recomputeContext.SourceFile, (int)recomputeContext.ChunkIndex, cancellationToken).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }
}
