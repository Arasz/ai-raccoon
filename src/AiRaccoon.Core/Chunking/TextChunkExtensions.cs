namespace AiRaccoon.Core.Chunking;

public static class TextChunkExtensions
{
    /// <summary>The stored `section` value for a chunk: its <see cref="TextChunk.Sections" /> joined
    /// with " | " (docs/adr/0048), or null when it holds no section.</summary>
    public static string? SectionLabel(this TextChunk chunk) =>
        chunk.Sections.Count == 0 ? null : string.Join(" | ", chunk.Sections);
}
