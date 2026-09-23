namespace AiRaccoon.Core.Chunking;

/// <summary>Line-granular, token-bounded splitter for plain text: the markdown chunker's packing without headings or fences.</summary>
public sealed class PlainTextChunker(TokenCount countTokens) : IPlainTextChunker
{
    private readonly MarkdownChunker _chunker = new(countTokens, markdown: false);

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        _chunker.Chunk(text, maxTokens, overlayTokens, countTokens);

    public IReadOnlyList<TextChunk> ChunkWithHeadings(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        _chunker.ChunkWithHeadings(text, maxTokens, overlayTokens, countTokens);
}
