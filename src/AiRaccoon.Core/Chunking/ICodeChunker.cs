namespace AiRaccoon.Core.Chunking;

/// <summary>
///     Splits source text into line-ranged chunks. Deliberately not <see cref="IChunker" />: that
///     interface returns prose strings, code needs the line range (docs/work/2026-08-21-code-search-implementation-plan.md §3.4).
/// </summary>
public interface ICodeChunker
{
    IReadOnlyList<CodeChunk> Chunk(string text);
}
