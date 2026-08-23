namespace AiRaccoon.Core.Chunking;

/// <summary>Splits text into token-bounded, fence-safe chunks; a pure function of its input.</summary>
public interface IChunker
{
    /// <param name="countTokens">Overrides the constructor-injected tokenizer for this call only —
    /// lets a caller that knows the embedding engine's real tokenizer (docs/adr/0036) get a chunk
    /// budget guaranteed against that tokenizer instead of the default one.</param>
    IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null);

    /// <summary>
    ///     <see cref="Chunk" /> plus each chunk's <see cref="TextChunk.HeadingPath" />. Default
    ///     implementation approximates the contract by re-parsing each chunk's own text via
    ///     <see cref="HeadingPathParser" /> — wrong when a chunk holds none of the section it
    ///     opens. <see cref="MarkdownChunker" /> overrides this with the accurate, single-pass
    ///     version (docs/adr/0048, #549/#550 amendment); other chunkers keep this default.
    /// </summary>
    IReadOnlyList<TextChunk> ChunkWithHeadings(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) =>
        Chunk(text, maxTokens, overlayTokens, countTokens)
            .Select(chunk => new TextChunk(chunk, HeadingPathParser.Parse(chunk)))
            .ToList();
}
