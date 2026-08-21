namespace AiRaccoon.Core.Chunking;

/// <summary>One code chunk with its 1-based, inclusive source line range.</summary>
public sealed record CodeChunk(string Text, int LineStart, int LineEnd);
