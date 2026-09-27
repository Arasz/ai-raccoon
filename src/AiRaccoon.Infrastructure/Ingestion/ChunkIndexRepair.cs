using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Re-derives each (ctx, source_file) partition's positions from the file on disk: the file's rows take their
///     document positions (-1 when the file no longer reproduces them, never a guess), notes citing the file follow
///     them, and total_chunks is the row count. Pure UPDATE; runs only when `repair chunk-index --apply` asks for it.
/// </summary>
public sealed class ChunkIndexRepair(IFileTypeMatcher fileTypeMatcher, IEmbeddingService embeddingService)
{
    private readonly ChunkPositionScanner _scanner = new(fileTypeMatcher, embeddingService);

    public async Task<ChunkIndexRepairReport> RunAsync(SqliteConnection connection, bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var (maxTokens, overlayTokens, countTokens) = await _scanner.BudgetAsync(connection, cancellationToken);

        var groups = (await connection.QueryAsync<long>(new CommandDefinition(
                $"""
                 SELECT MIN(id) FROM entries
                 WHERE source_file IS NOT NULL
                 GROUP BY {MemorySql.ContextKeyExpression("")}, source_file
                 """, cancellationToken: cancellationToken))).ToList();

        var repositioned = 0;
        var setUnknown = 0;
        var retotalled = 0;

        foreach (var memberId in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partition = await ChunkPositionScanner.PartitionAsync(connection, memberId, cancellationToken);
            var fileRows = partition.Where(row => row.IsFileRow).Select(row => new StoredChunk(row.Id, row.Hash)).ToList();
            var scan = _scanner.Scan(partition[0].SourceFile, fileRows, maxTokens, overlayTokens, countTokens);
            var moves = ChunkPositionScanner.Moves(partition, scan);
            var before = partition.ToDictionary(row => row.Id);
            foreach (var move in moves)
            {
                var row = before[move.Id];
                if (move.ChunkIndex == row.ChunkIndex)
                {
                    retotalled++;
                }
                else if (move.ChunkIndex < 0)
                {
                    setUnknown++;
                }
                else
                {
                    repositioned++;
                }
            }

            if (apply)
            {
                await ChunkPositionScanner.WriteAsync(connection, moves, cancellationToken);
            }
        }

        return new ChunkIndexRepairReport(groups.Count, repositioned, setUnknown, retotalled);
    }
}
