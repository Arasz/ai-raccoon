namespace AiRaccoon.Core.Chunking;

/// <summary>Splits text into token-bounded, fence-safe chunks; a pure function of its input.</summary>
public interface IChunker
{
    IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0);
}
