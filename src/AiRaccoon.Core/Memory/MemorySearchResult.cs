namespace AiRaccoon.Core.Memory;

/// <param name="ContentCosine">
///     The vector leg's raw content-embedding cosine similarity to the query — set only by the
///     dual-vector builder, distinct from <paramref name="Ranking" /> (which for that leg carries
///     the alpha-fused content/structure score used for ordering, see StructureFusion.Fused).
/// </param>
public sealed record MemorySearchResult(
    string Hash,
    double Ranking,
    string Path,
    string Snippet,
    string? SourceFile = null,
    int ChunkIndex = 0,
    int TotalChunks = 0,
    double? ContentCosine = null);
