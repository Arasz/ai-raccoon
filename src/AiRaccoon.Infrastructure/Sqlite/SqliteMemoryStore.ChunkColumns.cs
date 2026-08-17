using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Chunk-column maintenance (docs/plans/2026-08-08-search-knn-perf.md §3.3, GH #371): the write/delete-boundary helpers <see cref="SqliteMemoryStore" />'s write paths call.</summary>
public sealed partial class SqliteMemoryStore
{
    /// <summary>
    ///     Fills chunk_index/total_chunks for one (ctx, sourceFile) group after a write that can
    ///     change its membership. Only ever fills the -1 "unknown" sentinel — an append never
    ///     disturbs a position an authoritative writer already set.
    /// </summary>
    private static async Task RecomputeChunkColumnsAsync(SqliteConnection connection, string context,
        string projectId, string sourceFile, CancellationToken cancellationToken)
    {
        var ctx = MemorySql.ContextKeyFor(context, projectId);
        await connection.ExecuteAsync(
                Def(MemorySql.RecomputeChunkColumnsForContext, new { ctx, sourceFile }, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Renumbers survivors of one (ctx, sourceFile) group after a row is removed: shifts
    ///     chunk_index down past the deleted row's position and shrinks total_chunks, without
    ///     re-deriving anyone's position from id order.
    /// </summary>
    private static async Task CompactChunkColumnsAfterDeleteAsync(SqliteConnection connection, string context,
        string projectId, string sourceFile, int deletedIndex, CancellationToken cancellationToken)
    {
        var ctx = MemorySql.ContextKeyFor(context, projectId);
        await connection.ExecuteAsync(
                Def(MemorySql.CompactChunkColumnsAfterDelete, new { ctx, sourceFile, deletedIndex }, cancellationToken))
            .ConfigureAwait(false);
    }
}
