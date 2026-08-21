namespace AiRaccoon.Core.Memory.Code;

/// <summary>One code_entries chunk hit. Ranking is max-normalized per response, like MemorySearchResult (rank 1 == 1.0).</summary>
public sealed record CodeSearchResult(string Hash, double Ranking, string Path, string Snippet, int LineStart, int LineEnd);
