using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>One stored row of a file: the chunk, its content hash, and its document position.</summary>
public sealed record DocumentChunk(TextChunk Chunk, string Hash, int Position);

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
}
