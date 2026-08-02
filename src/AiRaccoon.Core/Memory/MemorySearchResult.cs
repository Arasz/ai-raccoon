namespace AiRaccoon.Core.Memory;

public sealed record MemorySearchResult(string Hash, int Seq, double Ranking, string Path, string Snippet);
