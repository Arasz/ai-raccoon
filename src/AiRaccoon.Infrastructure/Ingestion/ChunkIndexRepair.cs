using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Re-derives each (ctx, source_file) partition's positions from the file on disk: the file's rows take their
///     document positions (-1 when the file no longer reproduces them, never a guess), notes citing the file follow
///     them, and total_chunks is the row count. A partition an older chunking left in a valid order keeps it
///     (docs/adr/0123). Pure UPDATE; runs only when `repair chunk-index --apply` asks for it.
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
            var gone = await UnplaceableAsync(connection, partition, scan, cancellationToken);
            var moves = gone is not null
                ? ChunkPositionScanner.MovesKeepingStoredOrder(partition, gone)
                : ChunkPositionScanner.Moves(partition, scan);
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

    /// <summary>
    ///     The rows that go to unknown when the partition keeps the stored order an older chunking left
    ///     (docs/adr/0123): only the rows whose text no longer sits where their stored position puts it —
    ///     every other row keeps its stored position, holes and all. Null when the stored order is not one
    ///     this rule may keep: the file is unusable, the scan reproduces every row, the stored positions are
    ///     not 0..n-1 (a row already unknown may stand at -1 and keeps it), or the rows the scan reproduces
    ///     are not stored in document order.
    /// </summary>
    private static async Task<IReadOnlySet<long>?> UnplaceableAsync(SqliteConnection connection,
        IReadOnlyList<PartitionEntry> partition, ChunkPositionScan scan, CancellationToken cancellationToken)
    {
        var fileRows = partition.Where(row => row.IsFileRow)
            .OrderBy(row => row.ChunkIndex < 0).ThenBy(row => row.ChunkIndex).ThenBy(row => row.Id)
            .ToList();
        var unplaced = fileRows.Where(row => scan.PositionById[row.Id] < 0).Select(row => row.Id).ToList();
        if (!scan.FileUsable || unplaced.Count == 0)
        {
            return null;
        }

        var known = fileRows.Select(row => row.ChunkIndex).Where(index => index >= 0).ToList();
        if (known.Any(index => index >= fileRows.Count) || known.Distinct().Count() != known.Count)
        {
            return null;
        }

        // Only rows with a stored position prove it: one already at -1 has no place in the order, whatever
        // the scan found for it (the keep rule never resurrects it).
        var placed = fileRows.Where(row => row.ChunkIndex >= 0)
            .Select(row => scan.PositionById[row.Id]).Where(position => position >= 0).ToList();
        if (placed.Zip(placed.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            return null;
        }

        var stored = fileRows.Where(row => row.ChunkIndex >= 0).ToList();
        var values = (await connection.QueryAsync<(long Id, string Value)>(new CommandDefinition(
                "SELECT id AS Id, value AS Value FROM entries WHERE id IN @ids", new { ids = stored.Select(row => row.Id) },
                cancellationToken: cancellationToken)))
            .ToDictionary(row => row.Id, row => row.Value);

        // A kept row is one whose text still sits where its stored position puts it: located in the file as the
        // chunkers read it, in stored order (StoredOrderChain). Text gone, moved, or surviving only out of place is not.
        return StoredOrderChain.Unplaceable(
            [.. stored.Select(row => new StoredOrderRow(row.Id, values[row.Id], scan.PositionById[row.Id] >= 0))], scan.Content);
    }
}
