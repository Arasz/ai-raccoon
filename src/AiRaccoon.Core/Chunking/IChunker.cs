namespace AiRaccoon.Core.Chunking;

/// <summary>Splits text into token-bounded, fence-safe chunks; a pure function of its input.</summary>
public interface IChunker
{
    /// <param name="countTokens">Overrides the constructor-injected tokenizer for this call only —
    /// lets a caller that knows the embedding engine's real tokenizer (docs/adr/0036) get a chunk
    /// budget guaranteed against that tokenizer instead of the default one.</param>
    IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null);
}
