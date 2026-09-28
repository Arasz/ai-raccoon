using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Proven reassembly of a <c>memory_write</c> note's stored chunks: the rows join back into the body
///     their path names only when the join hashes to the path's sha256 stem. Thin wrapper over
///     <see cref="NoteTextOrder" />'s backtracking overlay search — never a greedy longest-match strip,
///     which eats live text where the body repeats at a chunk boundary.
/// </summary>
public static class ChunkReassembly
{
    /// <summary>The body exactly as written (its line endings restored), or null when no join of
    /// <paramref name="rows" /> hashes to the stem of <paramref name="path" />.</summary>
    public static string? TryMerge(string path, IReadOnlyList<NoteRow> rows)
    {
        Guard.IsNotNull(path);
        Guard.IsNotNull(rows);
        return NoteTextOrder.FindMerged(path, rows);
    }
}
