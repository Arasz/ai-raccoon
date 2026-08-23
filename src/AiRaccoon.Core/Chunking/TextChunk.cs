namespace AiRaccoon.Core.Chunking;

/// <summary>One text chunk with the heading path in force at its start (docs/adr/0048, #549/#550 amendment).</summary>
public sealed record TextChunk(string Text, string HeadingPath);
