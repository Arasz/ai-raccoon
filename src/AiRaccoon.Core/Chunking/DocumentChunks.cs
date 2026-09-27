using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>One stored row of a file: the chunk, its content hash, and its document position.</summary>
public sealed record DocumentChunk(TextChunk Chunk, string Hash, int Position);

/// <summary>A row of a (ctx, source_file) position partition: one of the file's own rows, or a note citing the file.</summary>
public sealed record PartitionRow(long Id, bool IsFileRow, long ChunkIndex);

public static class DocumentChunks
{
    /// <summary>The rows a file's chunks are stored as: a chunk repeated verbatim is one row at its first occurrence,
    /// so positions run 0..n-1 in document order and n is the row count.</summary>
    public static IReadOnlyList<DocumentChunk> Distinct(string path, IReadOnlyList<TextChunk> chunks)
    {
        Guard.IsNotNull(path);
        Guard.IsNotNull(chunks);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<DocumentChunk> rows = [];
        foreach (var chunk in chunks)
        {
            var hash = ContentHash.Of(path, chunk.Text);
            if (seen.Add(hash))
            {
                rows.Add(new DocumentChunk(chunk, hash, rows.Count));
            }
        }

        return rows;
    }

    /// <summary>
    ///     Positions for a (ctx, source_file) partition, whose total_chunks is its row count: the file's rows are
    ///     ranked in the document order <paramref name="filePositions" /> gives them (-1 for a row it lacks), or keep
    ///     their stored order when it is null, and every other row follows them in its stored order.
    /// </summary>
    public static IReadOnlyDictionary<long, long> PartitionPositions(IReadOnlyList<PartitionRow> partition,
        IReadOnlyDictionary<long, int>? filePositions)
    {
        Guard.IsNotNull(partition);

        var fileRows = InStoredOrder(partition.Where(row => row.IsFileRow)).ToList();
        var positions = new Dictionary<long, long>(partition.Count);
        if (filePositions is null)
        {
            for (var i = 0; i < fileRows.Count; i++)
            {
                positions[fileRows[i].Id] = i;
            }
        }
        else
        {
            var placed = fileRows
                .Select(row => (row.Id, Position: filePositions.TryGetValue(row.Id, out var position) ? position : -1))
                .ToList();
            var rank = 0;
            foreach (var (id, _) in placed.Where(row => row.Position >= 0).OrderBy(row => row.Position))
            {
                positions[id] = rank++;
            }

            foreach (var (id, _) in placed.Where(row => row.Position < 0))
            {
                positions[id] = -1;
            }
        }

        var start = fileRows.Count;
        foreach (var (id, index) in After(start, partition.Where(row => !row.IsFileRow)))
        {
            positions[id] = index;
        }

        return positions;
    }

    /// <summary>Positions for the rows citing a file that follow its first <paramref name="start" /> positions, in
    /// their stored order.</summary>
    public static IEnumerable<(long Id, long ChunkIndex)> After(long start, IEnumerable<PartitionRow> citing)
    {
        Guard.IsNotNull(citing);

        return InStoredOrder(citing).Select((row, i) => (row.Id, start + i));
    }

    /// <summary>Known positions ascending, then unknown ones (-1) by id.</summary>
    private static IOrderedEnumerable<PartitionRow> InStoredOrder(IEnumerable<PartitionRow> rows) =>
        rows.OrderBy(row => row.ChunkIndex < 0).ThenBy(row => row.ChunkIndex).ThenBy(row => row.Id);
}
