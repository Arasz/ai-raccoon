using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     GH #371: re-derives chunk_index/total_chunks per (ctx, source_file) group straight from the
///     document a still-present source_file names, instead of the row-id order the old recompute
///     left behind. A group whose source_file is missing, unreadable, or absent gets chunk_index =
///     -1 (position unknown) — never a guess. Pure UPDATE: never inserts or deletes a row.
///     <para>
///         This class itself never implements <see cref="AiRaccoon.Infrastructure.Maintenance.IMaintenanceJob" />
///         — <see cref="AiRaccoon.Infrastructure.Maintenance.ChunkIndexRepairJob" /> wraps it instead
///         (ADR-0075 amendment), registered on the maintenance list but on-demand only (<c>Interval</c>
///         is null): it only ever runs because a human explicitly requested it via
///         `ai-raccoon repair chunk-index --apply`, which now commits a repair_requests row through
///         the server rather than writing here directly. Never on a clock, so it still cannot run
///         unattended against a live bank on open (docs/plans/2026-08-08-search-knn-perf.md §3.3).
///     </para>
/// </summary>
public sealed class ChunkIndexRepair(IFileTypeMatcher fileTypeMatcher, ILocalTokenizer localTokenizer)
{
    private readonly ChunkPositionScanner _scanner = new(fileTypeMatcher, localTokenizer);

    public async Task<ChunkIndexRepairReport> RunAsync(SqliteConnection connection, bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var (maxTokens, overlayTokens, countTokens) = await _scanner.BudgetAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        var groups = (await connection.QueryAsync<GroupKey>(new CommandDefinition(
                $"""
                 SELECT DISTINCT {MemorySql.ContextKeyExpression("")} AS Ctx, source_file AS SourceFile
                 FROM entries
                 WHERE source_file IS NOT NULL
                 """, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        var groupsExamined = 0;
        var repositioned = 0;
        var setUnknown = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupsExamined++;

            var rows = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
                    $"""
                     SELECT id AS Id, hash AS Hash, chunk_index AS ChunkIndex
                     FROM entries
                     WHERE source_file = @sourceFile AND ({MemorySql.ContextKeyExpression("")}) = @ctx
                     """, new { sourceFile = group.SourceFile, ctx = group.Ctx }, cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();
            var totalChunks = rows.Count;

            var scan = _scanner.Scan(group.SourceFile, rows.Select(row => (row.Id, row.Hash)).ToList(),
                maxTokens, overlayTokens, countTokens);

            foreach (var row in rows)
            {
                var newIndex = scan.PositionById[row.Id];
                if (newIndex == row.ChunkIndex)
                {
                    continue;
                }

                if (newIndex < 0)
                {
                    setUnknown++;
                }
                else
                {
                    repositioned++;
                }

                if (apply)
                {
                    await connection.ExecuteAsync(new CommandDefinition(MemorySql.SetChunkPosition,
                            new { id = row.Id, chunkIndex = newIndex, totalChunks }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }
            }
        }

        return new ChunkIndexRepairReport(groupsExamined, repositioned, setUnknown);
    }

    // Plain classes, not records: Ctx is a computed SQL expression with no declared column type, and
    // an empty result set makes Microsoft.Data.Sqlite report it as byte[] rather than string —
    // property-set materialization tolerates that; record constructor-matching does not.
    private sealed class GroupKey
    {
        public string Ctx { get; set; } = "";

        public string SourceFile { get; set; } = "";
    }

    private sealed class GroupRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";

        public long ChunkIndex { get; set; }
    }
}
