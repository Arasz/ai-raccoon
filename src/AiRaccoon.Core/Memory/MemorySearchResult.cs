namespace AiRaccoon.Core.Memory;

public sealed record MemorySearchResult(
    string Hash,
    int Seq,
    double Ranking,
    string Path,
    string Snippet,
    string? SourceFile = null,
    int ChunkIndex = 0,
    int TotalChunks = 0);
