namespace AiRaccoon.Core.Chunking;

/// <summary>One text chunk with the heading path in force at its first contentful new unit ("" when
/// it has none) and the leaf of every section it holds content for (docs/adr/0048, #549/#550).</summary>
public sealed record TextChunk(string Text, string HeadingPath, IReadOnlyList<string> Sections);
